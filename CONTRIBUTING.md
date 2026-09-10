# 贡献指南（Contributing）

感谢你愿意帮助改进 ChenLink！无论是修 Bug、加功能、改进文档，都非常欢迎。

## 提交 Issue

- **Bug**：请说明操作系统版本、复现步骤、期望结果与实际结果，最好附上日志。
- **功能建议**：请描述使用场景和期望效果。
- **提问**：请先阅读 README，确认不是已解答的问题。

## 提交 PR

1. Fork 本仓库，基于最新的 `main` 新建分支（建议命名 `fix/xxx` 或 `feat/xxx`）。
2. 本地自测：
   ```powershell
   dotnet run --project src/ChenLinkServer -- --selftest
   ```
   确保输出 `SELFTEST PASS`。
3. 提交信息使用简洁的英文或中文描述，例如 `fix: 通道关闭时连接数可能变为负数`。
4. 发起 PR，说明改动原因与验证方式。

## 代码约定

- **尽量不新增依赖**：网络核心（`src/ChenLink/Engine`、`src/ChenLinkServer`）刻意保持“纯 BCL”，
  新增功能请优先使用 .NET 自带能力。
- **保持精简**：优先小而清晰的改动；不引入暂时用不到的抽象/配置。
- **格式**：4 空格缩进、UTF-8 编码、文本文件使用 LF 换行（由 `.gitattributes` 统一管理）。
- 涉及非平凡逻辑时，请在 PR 中说明你是如何验证的。

## 许可证

提交代码即表示你同意以 **Apache License 2.0** 授权你的贡献（见 [LICENSE](LICENSE)）。