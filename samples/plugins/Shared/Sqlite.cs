using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PluginShared;

/// <summary>
/// 插件自己的 SQLite 数据库（Windows 自带的 winsqlite3.dll，不需要额外的 DLL，NativeAOT 可用）。
/// 一个插件一个连接，所有操作加锁串行执行，可以在任何线程上调用。参数只支持 string、long、int、bool 与 null。
/// </summary>
internal sealed unsafe class SqliteDb : IDisposable
{
    private const string Lib = "winsqlite3";
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x4;
    private const int OpenFullMutex = 0x10000;
    private static readonly nint Transient = -1;

    private readonly Lock _gate = new();
    private nint _db;

    private SqliteDb(nint db) => _db = db;

    /// <summary>打开（不存在时创建）数据库并执行建表语句；失败时写入插件日志并返回 null。</summary>
    public static SqliteDb? Open(string path, string schema)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            nint handle = 0;
            int code;
            using (var p = new Utf8(path))
                code = sqlite3_open_v2(p.Ptr, &handle, OpenReadWrite | OpenCreate | OpenFullMutex, null);
            if (code != SqliteOk)
            {
                if (handle != 0)
                    sqlite3_close_v2(handle);
                Host.Log(Zq.LogError, $"打开数据库失败（{code}）：{path}");
                return null;
            }
            sqlite3_busy_timeout(handle, 3000);
            var db = new SqliteDb(handle);
            db.Execute(schema);
            return db;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
                                       or InvalidOperationException)
        {
            Host.Log(Zq.LogError, $"打开数据库失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>执行一条或多条语句（有参数时只能是一条）。失败时抛出 <see cref="InvalidOperationException"/>。</summary>
    public void Execute(string sql, params object?[] args)
    {
        lock (_gate)
        {
            if (args.Length == 0)
            {
                byte* error = null;
                int code;
                using (var p = new Utf8(sql))
                    code = sqlite3_exec(_db, p.Ptr, 0, 0, &error);
                if (code != SqliteOk)
                {
                    var message = Host.Read((nint)error) ?? code.ToString();
                    sqlite3_free(error);
                    throw new InvalidOperationException(message);
                }
                return;
            }
            Run(sql, args, _ => { });
        }
    }

    /// <summary>查询，每一行由 <paramref name="read"/> 转换。失败时抛出 <see cref="InvalidOperationException"/>。</summary>
    public List<T> Query<T>(string sql, Func<SqliteReader, T> read, params object?[] args)
    {
        var rows = new List<T>();
        lock (_gate)
            Run(sql, args, reader => rows.Add(read(reader)));
        return rows;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_db != 0)
                sqlite3_close_v2(_db);
            _db = 0;
        }
    }

    // 调用方持有 _gate
    private void Run(string sql, object?[] args, Action<SqliteReader> onRow)
    {
        nint stmt = 0;
        int code;
        using (var p = new Utf8(sql))
            code = sqlite3_prepare_v2(_db, p.Ptr, -1, &stmt, null);
        try
        {
            Check(code);
            for (var i = 0; i < args.Length; i++)
            {
                var index = i + 1;
                Check(args[i] switch
                {
                    null => sqlite3_bind_null(stmt, index),
                    string s => BindText(stmt, index, s),
                    long l => sqlite3_bind_int64(stmt, index, l),
                    int n => sqlite3_bind_int64(stmt, index, n),
                    bool b => sqlite3_bind_int64(stmt, index, b ? 1 : 0),
                    _ => throw new ArgumentException($"不支持的参数类型：{args[i]!.GetType().Name}"),
                });
            }
            var reader = new SqliteReader(stmt);
            while ((code = sqlite3_step(stmt)) == SqliteRow)
                onRow(reader);
            if (code != SqliteDone)
                Check(code);
        }
        finally
        {
            if (stmt != 0)
                sqlite3_finalize(stmt);
        }
    }

    private void Check(int code)
    {
        if (code != SqliteOk)
            throw new InvalidOperationException(Host.Read((nint)sqlite3_errmsg(_db)) ?? code.ToString());
    }

    // SQLITE_TRANSIENT：SQLite 复制文字，返回后即可释放
    private static int BindText(nint stmt, int index, string s)
    {
        using var p = new Utf8(s);
        return sqlite3_bind_text(stmt, index, p.Ptr, -1, Transient);
    }

    // 以 0 结尾的 UTF-8 字符串（非托管内存）
    private readonly struct Utf8(string s) : IDisposable
    {
        private readonly nint _p = Marshal.StringToCoTaskMemUTF8(s);

        public byte* Ptr => (byte*)_p;

        public void Dispose() => Marshal.FreeCoTaskMem(_p);
    }

    [DllImport(Lib)] private static extern int sqlite3_open_v2(byte* path, nint* db, int flags, byte* vfs);
    [DllImport(Lib)] private static extern int sqlite3_close_v2(nint db);
    [DllImport(Lib)] private static extern int sqlite3_busy_timeout(nint db, int ms);
    [DllImport(Lib)] private static extern int sqlite3_exec(nint db, byte* sql, nint callback, nint arg, byte** error);
    [DllImport(Lib)] private static extern void sqlite3_free(void* p);
    [DllImport(Lib)] private static extern byte* sqlite3_errmsg(nint db);
    [DllImport(Lib)] private static extern int sqlite3_prepare_v2(nint db, byte* sql, int bytes, nint* stmt, byte** tail);
    [DllImport(Lib)] private static extern int sqlite3_bind_null(nint stmt, int index);
    [DllImport(Lib)] private static extern int sqlite3_bind_text(nint stmt, int index, byte* text, int bytes, nint destructor);
    [DllImport(Lib)] private static extern int sqlite3_bind_int64(nint stmt, int index, long value);
    [DllImport(Lib)] private static extern int sqlite3_step(nint stmt);
    [DllImport(Lib)] private static extern int sqlite3_finalize(nint stmt);
    [DllImport(Lib)] internal static extern byte* sqlite3_column_text(nint stmt, int index);
    [DllImport(Lib)] internal static extern long sqlite3_column_int64(nint stmt, int index);
}

/// <summary>查询结果的当前行（只在 <see cref="SqliteDb.Query{T}"/> 的回调中有效）。</summary>
internal sealed unsafe class SqliteReader(nint stmt)
{
    public string Text(int column) => Host.Read((nint)SqliteDb.sqlite3_column_text(stmt, column)) ?? "";

    public long Long(int column) => SqliteDb.sqlite3_column_int64(stmt, column);
}
