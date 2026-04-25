using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace BluetoothResetService;

public class BluetoothResetWorker : BackgroundService
{
    private readonly string deviceKeyword = "Air Pro 6"; // ←←← 改成你的藍牙耳機關鍵字，例如 "Air Pro 6" 或 "PaMu" 
    private readonly EventLog _eventLog;
    public static readonly string _logFolder = new Syroot.Windows.IO.KnownFolder(Syroot.Windows.IO.KnownFolderType.Downloads).Path;
    private readonly object _logLock = new object();

    public BluetoothResetWorker()
    {
        _eventLog = new EventLog("Application")
        {
            Source = "BluetoothAutoResetService"
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log("藍牙耳機智慧重置服務已啟動 (只在驅動異常時才重置)");
        _ = Task.Run(async () => await CheckAndResetBluetoothAsync());

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(10000, stoppingToken); // 每10秒檢查一次，可自行調整
            }
        }
        finally
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Log("服務已停止。");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 系統從睡眠/休眠喚醒，開始檢查藍牙裝置狀態...");
            _ = Task.Run(async () => await CheckAndResetBluetoothAsync());
        }
    }

    private async Task CheckAndResetBluetoothAsync()
    {
        try
        {
            // 使用 Base64 EncodedCommand 方式（最穩定，不會有引號問題）
            string rawScript = $@"
            $keyword = '{deviceKeyword.Replace("'", "''")}'
            
            Write-Host '=== 詳細裝置狀態檢查 ==='
            
            $devices = Get-PnpDevice | Where-Object {{
                ($_.FriendlyName -like ""*$keyword*"") -or
                ($_.Class -eq 'Bluetooth') -or
                ($_.FriendlyName -like '*Bluetooth*')
            }} | Select-Object FriendlyName, InstanceId, Status, ConfigManagerErrorCode, Problem

            $devices | ForEach-Object {{
                $errorCode = if ($null -eq $_.ConfigManagerErrorCode) {{ 0 }} else {{ $_.ConfigManagerErrorCode }}
                $problemDesc = if ([string]::IsNullOrEmpty($_.Problem)) {{ '無' }} else {{ $_.Problem }}
                Write-Host ""裝置: $($_.FriendlyName) | Status: $($_.Status) | ErrorCode: $errorCode | Problem: $problemDesc""
            }}

            $needReset = $devices | Where-Object {{
                ($_.ConfigManagerErrorCode -ne 0 -and $null -ne $_.ConfigManagerErrorCode) -or
                ($_.Status -ne 'OK' -and $null -ne $_.Status) -or
                (-not [string]::IsNullOrEmpty($_.Problem))
            }}

            if ($needReset.Count -gt 0) {{
                'NEED_RESET'
            }} else {{
                'OK'
            }}
        ";

            // 轉成 Base64 後傳給 PowerShell -EncodedCommand
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(rawScript);
            string encodedCommand = Convert.ToBase64String(bytes);

            string psCommand = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}";

            string result = RunPowerShellCommandWithOutputEncoded(psCommand).Trim();

            bool hasIssue = result.Contains("NEED_RESET");

            Log($"檢查結果: {(hasIssue ? "發現驅動異常 → 準備重置" : "目前判斷為正常 → 跳過重置")}");

            if (hasIssue)
            {
                Log("偵測到藍牙驅動異常，開始執行重置...");
                await PerformResetAsync();
            }
            else
            {
                Log("藍牙裝置狀態正常，跳過重置。");
            }
        }
        catch (Exception ex)
        {
            Log($"檢查過程發生錯誤: {ex.Message}", EventLogEntryType.Error);
        }
    }
    private string RunPowerShellCommandWithOutputEncoded(string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "powershell.exe";
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            // 關鍵優化：使用非同步讀取，避免死鎖
            process.Start();

            // 非同步讀取輸出和錯誤
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            // 設定超時（最多等 30 秒，避免無限卡住）
            if (!process.WaitForExit(30000))  // 30秒超時
            {
                try { process.Kill(); } catch { }
                Log("PowerShell 執行超時（超過30秒），已強制終止。", EventLogEntryType.Warning);
                return string.Empty;
            }

            string output = outputTask.Result;
            string error = errorTask.Result;

            if (!string.IsNullOrEmpty(error))
                Log($"PowerShell 警告/錯誤: {error.Trim()}", EventLogEntryType.Warning);

            return output;
        }
        catch (Exception ex)
        {
            Log($"執行 PowerShell 失敗: {ex.Message}", EventLogEntryType.Error);
            return string.Empty;
        }
    }
    private async Task PerformResetAsync()
    {
        try
        {
            Log("正在重啟 Bluetooth Support Service (bthserv)...");
            RunPowerShellCommand("Restart-Service -Name bthserv -Force");
            await Task.Delay(2500);

            // 使用更簡單、安全的重置方式（避免 Remove-PnpDevice 造成的長時間卡住）
            string rawResetScript = $@"
            $keyword = '{deviceKeyword.Replace("'", "''")}'
            
            Write-Host '=== 開始藍牙重置 ==='
            
            # 只重置 Bluetooth Radio 適配器（最有效的方式）
            $radio = Get-PnpDevice | Where-Object {{ $_.Class -eq 'Bluetooth' -and $_.FriendlyName -like '*Bluetooth*' }}
            foreach ($dev in $radio) {{
                try {{
                    Write-Host ""正在重置 Bluetooth 適配器: $($dev.FriendlyName)""
                    Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 2
                    Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
                    Write-Host ""Bluetooth 適配器重置完成: $($dev.FriendlyName)""
                }} catch {{
                    Write-Host ""適配器重置失敗: $($dev.FriendlyName)""
                }}
            }}

            # 簡單處理耳機裝置（只 Disable + Enable，不 Remove）
            $earphones = Get-PnpDevice | Where-Object {{ $_.FriendlyName -like ""*$keyword*"" }}
            foreach ($dev in $earphones) {{
                try {{
                    Write-Host ""正在重置耳機: $($dev.FriendlyName)""
                    Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 2
                    Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
                    Write-Host ""耳機重置完成: $($dev.FriendlyName)""
                }} catch {{ }}
            }}
            
            Write-Host '=== 重置腳本執行完畢 ==='
        ";

            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(rawResetScript);
            string encodedCommand = Convert.ToBase64String(bytes);
            string psCommand = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}";

            Log("開始執行簡化版藍牙重置腳本...");
            RunPowerShellCommandWithOutputEncoded(psCommand);

            await Task.Delay(6000);   // 給系統 6 秒重新初始化

            Log("藍牙重置流程完成！請等待 10~20 秒後嘗試連接你的 Air Pro 6 耳機。");
            Log("如果還是無法連接，建議手動在裝置管理員對「Intel(R) Wireless Bluetooth」右鍵 → 解除安裝裝置 → 掃描硬體變更。");
        }
        catch (Exception ex)
        {
            Log($"重置過程發生錯誤: {ex.Message}", EventLogEntryType.Error);
        }
    }

    private void RunPowerShellCommand(string command)
    {
        RunPowerShellCommandWithOutput(command);
    }

    private string RunPowerShellCommandWithOutput(string command)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "powershell.exe";
            process.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(error))
                Log($"PowerShell 警告/錯誤: {error.Trim()}", EventLogEntryType.Warning);

            return output;
        }
        catch (Exception ex)
        {
            Log($"執行 PowerShell 失敗: {ex.Message}", EventLogEntryType.Error);
            return string.Empty;
        }
    }

    // ====================== 修改後的 Log 方法（同時寫入 EventLog + Console + TXT） ======================
    private void Log(string message, EventLogEntryType type = EventLogEntryType.Information)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string logMsg = $"{timestamp} | {message}";

        // 1. 寫入 Windows Event Log
        try { _eventLog.WriteEntry(logMsg, type); } catch { }

        // 2. 寫入 Console
        Console.WriteLine(logMsg);

        // 3. 寫入 TXT 每日記錄檔
        try
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            string fileName = $"BluetoothResetService_{today}.log";
            string fullPath = Path.Combine(_logFolder, fileName);

            if (!Directory.Exists(_logFolder))
            {
                Directory.CreateDirectory(_logFolder);
            }

            lock (_logLock)
            {
                using (StreamWriter sw = File.AppendText(fullPath))
                {
                    sw.WriteLine(logMsg);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{timestamp} | [寫入 TXT 失敗] {ex.Message}");
        }
    }

    public override void Dispose()
    {
        _eventLog?.Dispose();
        base.Dispose();
    }
}