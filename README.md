# ReleaseSync

适用于 GitHub 仓库的 Release 同步器，支持定时从多个仓库拉取/更新最新release文件同步到本地。

## 使用

1. 从 [Release](https://github.com/greepar/ReleaseSync/releases) 下载对应系统的文件。
2. 运行后会自动生成 config.toml，按照里面的注释进行配置。

## 配置
config.toml文件支持热重载
```toml
# 默认下载到程序目录的 release 文件夹；需要自定义时取消下一行注释
# destination_root = "./my-downloads"
check_interval_minutes = 15 # 每 15 分钟检查一次；默认值是 1440（每天）
# proxy = "http://127.0.0.1:7890" # 可选；也支持 https:// 代理

[[software]]
name = "example-linux"
repo = "owner/repository"
asset_pattern = "example-*-linux-x64.tar.gz"
destination = "example"
file_name = "example.tar.gz"

[[software]]
name = "example-windows"
repo = "owner/repository"
asset_pattern = "example-*-win-x64.zip"
destination = "example/windows"
# 不写 file_name 时，使用 GitHub 附件原名
```

## 开源协议
[MIT](LICENSE)
