# Deepseek Copilot

国内Copilot按键太鸡肋用不了？用DeepSeek夺舍它。

## Feature

基于Webview2 + .Net 10构建，占用体积极小。

单例管理Webview进程并且可自动销毁，节约内存。

自动打开chat.deepseek.com网址，无需Token。

多样的自定义功能。

## 外部调用接口

提供本地命名管道接口，外部程序可调用 AI：调用后自动打开侧边栏，把问题填入输入框并模拟回车发送。

- 管道名：`DeepSeekCopilot-Ask`
- 协议：发送一行 UTF-8 文本（`\n` 结尾），可为纯文本或 JSON `{"question":"..."}`，收到一行回复 `OK` / `FAILED` / `EMPTY`

### C# 调用示例

```csharp
using System.IO.Pipes;
using System.Text;

var question = "帮我写一段C#代码";

using var client = new NamedPipeClientStream(".", "DeepSeekCopilot-Ask", PipeDirection.InOut);
client.Connect(2000);

var bytes = Encoding.UTF8.GetBytes(question + "\n");
client.Write(bytes);
client.Flush();

var buf = new byte[256];
var sb = new StringBuilder();
while (true)
{
    var n = client.Read(buf, 0, buf.Length);
    if (n <= 0) break;
    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
    if (sb.ToString().Contains('\n')) break;
}
Console.WriteLine(sb.ToString().Trim());
```

### PowerShell 调用示例

```powershell
$p = New-Object System.IO.Pipes.NamedPipeClientStream(".", "DeepSeekCopilot-Ask", [System.IO.Pipes.PipeDirection]::InOut)
$p.Connect(2000)
$w = New-Object System.IO.StreamWriter($p)
$r = New-Object System.IO.StreamReader($p)
$w.AutoFlush = $true
$w.WriteLine("帮我写一段C#代码")
Write-Host $r.ReadLine()
```

### 命令行调用

程序已运行时，直接传参即可转发给现有实例（若未运行会提示）：

```bash
deepseek-copilot.exe "帮我写一段C#代码"
deepseek-copilot.exe --ask "帮我写一段C#代码"
```

> 注意：请确保 DeepSeek Copilot 已登录 chat.deepseek.com 且侧边栏能正常加载。


















