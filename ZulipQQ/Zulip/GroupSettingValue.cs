using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZulipQQ.Zulip;

/// <summary>
/// Zulip 的“组设置值”：可以是一个用户组 id，也可以是匿名组（直接成员 + 直接子组）。
/// JSON 中分别是数字，或 <c>{"direct_members":[…],"direct_subgroups":[…]}</c>。
/// </summary>
[JsonConverter(typeof(GroupSettingValueConverter))]
public sealed record GroupSettingValue(long? GroupId, IReadOnlyList<long> DirectMembers, IReadOnlyList<long> DirectSubgroups)
{
    public static GroupSettingValue Group(long groupId) => new(groupId, [], []);

    public static GroupSettingValue Anonymous(IReadOnlyList<long> members, IReadOnlyList<long> subgroups) =>
        new(null, members, subgroups);

    public bool Equals(GroupSettingValue? other)
    {
        if (other is null)
            return false;
        if (GroupId is not null || other.GroupId is not null)
            return GroupId == other.GroupId;
        return Same(DirectMembers, other.DirectMembers) && Same(DirectSubgroups, other.DirectSubgroups);
    }

    public override int GetHashCode()
    {
        if (GroupId is { } id)
            return id.GetHashCode();
        var hash = new HashCode();
        foreach (var m in DirectMembers)
            hash.Add(m);
        hash.Add(-1L);
        foreach (var g in DirectSubgroups)
            hash.Add(g);
        return hash.ToHashCode();
    }

    private static bool Same(IReadOnlyList<long> a, IReadOnlyList<long> b)
    {
        if (a.Count != b.Count)
            return false;
        var set = new HashSet<long>(a);
        foreach (var x in b)
        {
            if (!set.Contains(x))
                return false;
        }
        return true;
    }
}

public sealed class GroupSettingValueConverter : JsonConverter<GroupSettingValue>
{
    public override GroupSettingValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return GroupSettingValue.Group(reader.GetInt64());
            case JsonTokenType.StartObject:
                List<long> members = [];
                List<long> subgroups = [];
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString();
                    reader.Read();
                    if (name == "direct_members")
                        ReadIds(ref reader, members);
                    else if (name == "direct_subgroups")
                        ReadIds(ref reader, subgroups);
                    else
                        reader.Skip();
                }
                return GroupSettingValue.Anonymous(members, subgroups);
            default:
                throw new JsonException($"无法解析组设置值：{reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, GroupSettingValue value, JsonSerializerOptions options)
    {
        if (value.GroupId is { } id)
        {
            writer.WriteNumberValue(id);
            return;
        }

        writer.WriteStartObject();
        writer.WriteStartArray("direct_members");
        foreach (var m in value.DirectMembers)
            writer.WriteNumberValue(m);
        writer.WriteEndArray();
        writer.WriteStartArray("direct_subgroups");
        foreach (var g in value.DirectSubgroups)
            writer.WriteNumberValue(g);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void ReadIds(ref Utf8JsonReader reader, List<long> target)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            target.Add(reader.GetInt64());
    }
}
