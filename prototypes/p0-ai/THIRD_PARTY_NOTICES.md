# Third-party notices

CodexParser.cs 中的累计流与压缩去重算法移植自 [TokenTracker](https://github.com/xiufengsun/TokenTracker/tree/68c22580e0a62152cc7cfa2c476e36bf216904dc)，对应 src/lib/codex-token-usage.js 和 src/lib/codex-usage-record.js，已改写为 C# 并保留以下许可。

## TokenTracker

MIT License

Copyright (c) 2026 xiufengsun

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## .NET / SQLite packages

Microsoft.Data.Sqlite、SQLitePCLRaw及原生SQLite随NuGet包保留各自许可；版本与传递依赖见packages.lock.json。正式打包仍须把各包原文许可纳入发行物，不以本说明替代发布许可证清单。

依赖审计修订：显式引用SQLitePCLRaw.bundle_e_sqlite3 3.0.5以移除受GHSA-2m69-gcr7-jv3q影响的原生包，锁文件实际采用SQLite 3.53.4。SQLitePCLRaw为Apache-2.0，原生SQLite为public domain；各包原文许可随NuGet保留。
