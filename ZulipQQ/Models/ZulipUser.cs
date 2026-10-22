using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Models;

/// <summary>Zulip 用户（含机器人、已停用用户）。收到 realm_user 事件时就地更新。</summary>
public sealed class ZulipUser
{
    public long Id { get; init; }
    public string Email { get; set; } = "";

    /// <summary>真实邮箱，只在有权查看时有值。</summary>
    public string? DeliveryEmail { get; set; }

    public string FullName { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public bool IsBot { get; set; }
    public int? BotType { get; set; }

    /// <summary>100 所有者 / 200 管理员 / 300 版主 / 400 成员 / 600 访客。</summary>
    public int Role { get; set; } = 400;

    public bool IsActive { get; set; } = true;
    public string Timezone { get; set; } = "";
    public Dictionary<string, ProfileValueDto> ProfileData { get; set; } = [];

    public static ZulipUser FromDto(UserDto dto, bool isActive) => new()
    {
        Id = dto.UserId,
        Email = dto.Email,
        DeliveryEmail = dto.DeliveryEmail,
        FullName = dto.FullName,
        AvatarUrl = dto.AvatarUrl,
        IsBot = dto.IsBot,
        BotType = dto.BotType,
        Role = dto.Role,
        IsActive = isActive,
        Timezone = dto.Timezone,
        ProfileData = dto.ProfileData ?? [],
    };

    /// <summary>应用 realm_user update 事件的 <c>person</c>（只包含变化的字段）。</summary>
    public void ApplyUpdate(JsonElement person)
    {
        foreach (var p in person.EnumerateObject())
        {
            switch (p.Name)
            {
                case "full_name":
                    FullName = p.Value.GetString() ?? FullName;
                    break;
                case "avatar_url":
                    AvatarUrl = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                    break;
                case "role":
                    Role = p.Value.GetInt32();
                    break;
                case "timezone":
                    Timezone = p.Value.GetString() ?? "";
                    break;
                case "new_email":
                    Email = p.Value.GetString() ?? Email;
                    break;
                case "delivery_email":
                    DeliveryEmail = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                    break;
                case "is_active":
                    IsActive = p.Value.GetBoolean();
                    break;
                case "custom_profile_field":
                    ApplyProfileField(p.Value);
                    break;
            }
        }
    }

    // value 为 null 表示清除该字段
    private void ApplyProfileField(JsonElement field)
    {
        var id = field.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
        if (!field.TryGetProperty("value", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            ProfileData.Remove(id);
            return;
        }
        ProfileData[id] = ZulipJson.Deserialize<ProfileValueDto>(field);
    }
}
