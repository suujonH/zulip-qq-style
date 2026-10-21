# 测试用 Zulip 服务器（WSL + docker-zulip）

开发和验证使用本机 WSL Ubuntu 中的 [docker-zulip](https://github.com/zulip/docker-zulip)。
**本文档不包含任何密码或密钥**：secrets 与测试账号密码只保存在 WSL 中，不进入仓库。

以下命令都在 WSL 中以 root 执行（`wsl -d Ubuntu -u root`）。

## 1. 安装

```bash
apt-get install -y docker.io docker-compose-v2 git
systemctl enable --now docker
git clone --depth 1 https://github.com/zulip/docker-zulip /opt/docker-zulip
cd /opt/docker-zulip
```

## 2. 配置

`.env`（`chmod 600`）中写入随机生成的 secrets：

```bash
for k in POSTGRES MEMCACHED RABBITMQ REDIS EMAIL; do echo "ZULIP__${k}_PASSWORD=$(openssl rand -hex 24)"; done > .env
echo "ZULIP__SECRET_KEY=$(openssl rand -hex 48)" >> .env
chmod 600 .env
```

`compose.override.yaml`：secrets 的声明与 `compose.override.yaml.example` 相同，`zulip` 服务改为：

```yaml
services:
  zulip:
    ports: !override
      - "127.0.0.1:8443:443"
    environment:
      SETTING_EXTERNAL_HOST: "localhost.localdomain:8443"
      SETTING_ZULIP_ADMINISTRATOR: "admin@zulipqq.test"
      CERTIFICATES: "self-signed"
```

说明：

- `EXTERNAL_HOST` 必须带域名部分，`localhost` 会被 Zulip 的系统检查拒绝；`localhost.localdomain` 在 Windows 上解析为 127.0.0.1，无需修改 hosts。
- 只映射 443 → 8443，并只绑定 127.0.0.1。默认的 25 / 80 / 443 不对外暴露。
- 证书是自签名的，客户端登录时需在“高级”中勾选“允许不受信任的证书”。

## 3. 初始化与启动

```bash
docker compose pull
docker compose run --rm zulip app:init
docker compose up -d --wait
```

WSL 使用 mirrored 网络时，若启动报 `failed to bind host port ... address already in use`，
通常是上一个容器留下的 TIME_WAIT 连接，约 2 分钟后重试即可。WSL 空闲时会关闭虚拟机，
开发期间可在 Windows 中保持一个 `wsl -d Ubuntu -- sleep infinity` 进程。

## 4. 创建组织与所有者

```bash
M() { docker compose exec -T -u zulip zulip /home/zulip/deployments/current/manage.py "$@"; }
# 密码写入文件后复制进容器，避免出现在命令行中
docker compose cp owner.pw zulip:/tmp/owner.pw && docker compose exec -T zulip chown zulip /tmp/owner.pw
M create_realm --string-id "" --password-file /tmp/owner.pw --automated "ZulipQQ 测试" owner@zulipqq.test "群主大大"
# 允许所有者通过 API 创建用户（seed 脚本使用）
M change_user_role -r "" owner@zulipqq.test can_create_users
docker compose exec -T zulip rm -f /tmp/owner.pw
```

验证：`curl -k https://localhost.localdomain:8443/api/v1/server_settings`。

## 5. 种子数据

在 Windows 的 PowerShell 7 中：

```powershell
./tools/seed-zulip.ps1 -OwnerPassword <所有者密码> -UserPassword <测试用户密码> -OutFile $env:TEMP\zulip-keys.json
```

生成内容：

| 类别 | 内容 |
|---|---|
| 用户 | `u01`～`u12@zulipqq.test`（中文昵称） |
| 频道 | 开发、游戏、日常（公开）；公告（私有，所有者与 u01～u04） |
| 消息 | 每个频道 3～4 个话题、每个话题 25 条，包含 @提及、引用、代码块、列表、表情；每个频道一个文件和一张图片 |
| 私聊 | 与 u01～u05 的一对一私聊、2 个多人私聊、与自己的私聊、一个压缩包 |
| 所有者状态 | 历史消息已读；“开发 / 构建失败”约 40 条未读（含 @我）；“游戏 / 周末开黑”有 @全体成员；“日常 / 宠物”有新文件；星标 10 条；表情回应；1 个草稿 |
| 话题状态 | “开发 / ✔ 代码评审”已解决；“游戏 / 吐槽”已静音 |

脚本是幂等的：重复运行不会产生重复数据。频道消息、附件、星标、草稿、已解决和静音可以在中途失败后重新运行补齐；
私聊和“未读”这两批数据以第一条消息作为完成标记，若在这两步中途失败，重新运行不会补齐剩余部分，
需要时可删除测试组织后重新创建。
