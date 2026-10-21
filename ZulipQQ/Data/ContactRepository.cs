using System.Collections.Generic;
using System.Linq;
using ZulipQQ.Models;

namespace ZulipQQ.Data;

/// <summary>好友分组、分组成员关系和好友备注。一个用户最多属于一个自定义分组。</summary>
public sealed class ContactRepository(LocalDb db, long accountId)
{
    /// <summary>按 sort_order 排列的自定义分组。</summary>
    public List<FriendGroup> LoadGroups() =>
        db.Query("SELECT id, name FROM contact_group WHERE account_id = @a ORDER BY sort_order, id;",
            r => new FriendGroup(r.GetInt64(0), r.GetString(1)), ("@a", accountId));

    /// <summary>用户 id → 分组 id。</summary>
    public Dictionary<long, long> LoadMembership() =>
        db.Query("SELECT user_id, group_id FROM contact_group_member WHERE account_id = @a;",
                r => (UserId: r.GetInt64(0), GroupId: r.GetInt64(1)), ("@a", accountId))
            .ToDictionary(x => x.UserId, x => x.GroupId);

    public Dictionary<long, string> LoadRemarks() =>
        db.Query("SELECT user_id, remark FROM user_remark WHERE account_id = @a;",
                r => (UserId: r.GetInt64(0), Remark: r.GetString(1)), ("@a", accountId))
            .ToDictionary(x => x.UserId, x => x.Remark);

    /// <summary>新分组排在最后。</summary>
    public FriendGroup AddGroup(string name)
    {
        long id = 0;
        db.InTransaction(() =>
        {
            var order = db.Scalar<long?>("SELECT MAX(sort_order) FROM contact_group WHERE account_id = @a;", ("@a", accountId)) ?? 0;
            db.Execute("INSERT INTO contact_group(account_id, name, sort_order) VALUES(@a, @name, @order);",
                ("@a", accountId), ("@name", name), ("@order", order + 1));
            id = db.Scalar<long>("SELECT last_insert_rowid();");
        });
        return new FriendGroup(id, name);
    }

    public void RenameGroup(long groupId, string name) =>
        db.Execute("UPDATE contact_group SET name = @name WHERE account_id = @a AND id = @id;",
            ("@a", accountId), ("@id", groupId), ("@name", name));

    /// <summary>删除分组及其成员关系（成员回到“我的好友”）。</summary>
    public void DeleteGroup(long groupId)
    {
        db.InTransaction(() =>
        {
            db.Execute("DELETE FROM contact_group_member WHERE account_id = @a AND group_id = @id;", ("@a", accountId), ("@id", groupId));
            db.Execute("DELETE FROM contact_group WHERE account_id = @a AND id = @id;", ("@a", accountId), ("@id", groupId));
        });
    }

    /// <summary>把用户移到分组；<paramref name="groupId"/> 为 null 表示移回“我的好友”。</summary>
    public void SetMembership(IEnumerable<long> userIds, long? groupId)
    {
        db.InTransaction(() =>
        {
            foreach (var userId in userIds)
            {
                if (groupId is { } id)
                {
                    db.Execute("""
                        INSERT INTO contact_group_member(account_id, user_id, group_id) VALUES(@a, @u, @g)
                        ON CONFLICT(account_id, user_id) DO UPDATE SET group_id = excluded.group_id;
                        """, ("@a", accountId), ("@u", userId), ("@g", id));
                }
                else
                {
                    db.Execute("DELETE FROM contact_group_member WHERE account_id = @a AND user_id = @u;", ("@a", accountId), ("@u", userId));
                }
            }
        });
    }

    /// <summary>设置备注；null 表示清除。</summary>
    public void SetRemark(long userId, string? remark)
    {
        if (remark is null)
        {
            db.Execute("DELETE FROM user_remark WHERE account_id = @a AND user_id = @u;", ("@a", accountId), ("@u", userId));
            return;
        }
        db.Execute("""
            INSERT INTO user_remark(account_id, user_id, remark) VALUES(@a, @u, @r)
            ON CONFLICT(account_id, user_id) DO UPDATE SET remark = excluded.remark;
            """, ("@a", accountId), ("@u", userId), ("@r", remark));
    }
}
