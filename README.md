<div align="center">
  <h1>Bluetooth 耳機驅動自動重置服務</h1>
  <p><strong>藍牙耳機驅動異常自動偵測與修復工具</strong></p>
  
  **[繁體中文](#繁體中文)** &nbsp;&nbsp;•&nbsp;&nbsp;
</div>

---

## 繁體中文

**藍牙耳機驅動異常自動偵測與修復工具**（Windows 專用）

這個背景服務會每 10 秒檢查一次你的藍牙耳機（預設關鍵字為 "Air Pro 6"），當偵測到驅動異常時，會自動執行重置，解決常見的藍牙耳機連接問題。

### 主要功能
- 偵測到驅動異常（ErrorCode ≠ 0、Status ≠ OK、有 Problem）時自動處理
- 重啟 Bluetooth Support Service (`bthserv`)
- 對 Bluetooth Radio 適配器與耳機執行 **Disable → Enable** 重置
- 系統從睡眠/休眠喚醒時自動檢查
- 詳細記錄到 Windows 事件檢視器與下載資料夾的 log 檔案
- 只在真正異常時才重置，不會無謂干擾

### 解決問題
耳機無法連接、頻繁斷線、音質異常、裝置管理員出現黃色驚嘆號等常見藍牙驅動問題。

---

### 使用方式

#### 1. 修改耳機關鍵字（重要！）
打開 `BluetoothResetService/BluetoothResetWorker.cs`，修改第 8 行：

```csharp
private readonly string deviceKeyword = "你的耳機名稱";   // 例如 "Air Pro 6" 或 "PaMu" 或 "WH-1000XM5"
```

#### 2. 建置專案

使用 Visual Studio 開啟專案
選擇 Build → Build Solution 進行建置
找到輸出的執行檔 BluetoothResetService.exe
（通常位於 bin\Debug\net8.0-windows 或 bin\Release\net8.0-windows 資料夾）


#### 2. 裝為 Windows 服務（必須以系統管理員身分執行）
開啟 PowerShell 或命令提示字元（以系統管理員身分執行），然後執行以下指令：

```csharp
# 安裝服務（建議設定為開機自動啟動）
sc.exe create "BluetoothResetService" binPath= "C:\你的完整路徑\BluetoothResetService.exe" start= auto DisplayName= "藍牙耳機驅動自動重置服務"

# 啟動服務
sc.exe start "BluetoothResetService"

# 停止服務
sc.exe stop "BluetoothResetService"

# 刪除服務（解除安裝）
sc.exe delete "BluetoothResetService"

# 查看服務狀態
sc.exe query "BluetoothResetService"

```
