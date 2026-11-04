using System;
using System.Collections.Generic;

namespace ZulipQQ.Controls.Virtualization;

// 移植的 Avalonia 代码用到的内部工具方法（Avalonia 中为 internal），实现与 Avalonia 12.1.4 相同

internal static class MathUtilities
{
    private const double DoubleEpsilon = 2.2204460492503131e-016;

    public static bool AreClose(double value1, double value2)
    {
        if (value1 == value2)
            return true;
        var eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * DoubleEpsilon;
        var delta = value1 - value2;
        return -eps < delta && eps > delta;
    }

    public static bool GreaterThanOrClose(double value1, double value2) => value1 > value2 || AreClose(value1, value2);

    public static bool IsZero(double value) => Math.Abs(value) < 10.0 * DoubleEpsilon;
}

internal static class ListExtensions
{
    public static void InsertMany<T>(this List<T> list, int index, T item, int count)
    {
        var repeat = new T[count];
        Array.Fill(repeat, item);
        list.InsertRange(index, repeat);
    }
}
