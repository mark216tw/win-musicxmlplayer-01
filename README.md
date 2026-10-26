# Windows MusicXML 真實音源播放器 (WinMusicXmlPlayer)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6)](https://dotnet.microsoft.com/)
[![Framework](https://img.shields.io/badge/.NET-8.0%20WPF-512BD4)](https://dotnet.microsoft.com/)

**WinMusicXmlPlayer** 是一款專為 Windows 平台打造的輕量、高效能 **MusicXML (`.musicxml` / `.xml` / `.mxl`)** 樂譜視覺化與真實樂器取樣播放桌面應用程式。內建純 C# SoundFont 2 即時波表合成引擎與低延遲 WASAPI 音訊串流，結合雙模式互動鋼琴卷軸與 88 鍵動態鋼琴模擬鍵盤，支援多聲部獨立混音與即時動態主題外觀切換。

---

## 📌 核心功能特色

1. **完整 MusicXML 樂譜解析與反覆記號展開**
   - 支援標準 `.musicxml`、`.xml` 與壓縮格式 `.mxl`。
   - 支援自動展開反覆記號（`Repeat Forward / Backward`）與一二房結尾（`1st / 2nd Ending`），並可於工具列隨時勾選切換是否展開。
   - 完整詮釋速度變化（Tempo）、拍號、調號、力度記號（`ppp` ~ `fff`、`sfp`）、連音線（Tie）、圓滑線（Slur）、斷奏（Staccato）、延音（Tenuto）、琶音（Arpeggiate）與裝飾音（Trill / Mordent / Turn / Grace Note）。

2. **真實樂器取樣合成、雙槽音源分流與聲部獨立混音控制**
   - 內建 `TimGM6mb.sf2` General MIDI 128 種標準真實樂器取樣音源庫，並支援**「鋼琴專用音源 (.sf2)」與「通用樂器音源 (.sf2)」雙槽音源分流同步混音**：當掛載鋼琴專用音源時，鋼琴家族聲部（GM 001–006）自動路由至鋼琴合成器，其餘樂器使用通用合成器。
   - 內建 **「音源設定與下載建議 (`Ctrl+M`)」** 視窗，提供精選免費開源高品質 `.sf2` 音源推薦清單，支援一鍵「🌐 開啟網頁」或「📋 複製網址」。
   - 自動依據樂譜配器推斷對應樂器（平台鋼琴、主奏/第一/第二小提琴、中提琴、大提琴、長笛、雙簧管、單簧管、法國號等）與立體聲空間定位（Pan）。
   - 針對雙譜表（如鋼琴左右手）或同譜表多聲部自動分離為獨立混音軌道。
   - 每個聲部提供獨立 **靜音 (`M`)**、**獨奏 (`S`)**、**128 種 GM 音色即時切換**、**0%–150% 獨立音量滑桿**、**卷軸顯示開關**、**即時發聲強度動態條** 與 **一鍵重置**。

3. **雙模式互動鋼琴卷軸與 88 鍵動態鋼琴鍵盤**
   - **水平時間軸卷軸 (`⇄ 水平時間軸卷軸`)** 與 **垂直下落瀑布流 (`⬇ 垂直下落瀑布流`)** 單鍵即時切換。
   - 支援於卷軸畫布直接**按住滑鼠左鍵左右滑動調整播放時間位置 (Scrubbing)**，以及使用滑鼠滾輪或右下角按鈕無段縮放時間視野。
   - 底部配置 88 鍵（`A0` ~ `C8`）全尺寸動態鋼琴鍵盤，具備同音連續敲擊自動回彈間隙（Re-articulation Gap）與聲部代表色對應顯示。

4. **即時動態主題系統與個人化偏好設定**
   - **顯示模式**：支援 `系統`（預設，自動跟隨 Windows 深淺色設定）、`淺色`、`深色` 即時切換。
   - **視窗樣式主題**：內建 `現代圓潤`、`專業俐落`、`主題炫彩`、`古典音樂廳` 四種視窗與面板風格，並整合 Windows DWM 原生標題列與外框染色。
   - **主題色彩與單一 Hue 滑桿雙向連動**：提供 6 款活潑預設色塊（晴空蔚藍、珊瑚粉橘、向日葵金、薄荷翠綠、薰衣草紫、蜜桃玫瑰）與 `0°–360°` 全彩單一 Hue 滑桿，點擊或拖曳立即渲染全介面。

---

## 📂 專案文件導覽

完整中文技術與使用文件位於根目錄與 [`docs/`](docs/) 目錄：

| 文件名稱 | 檔案路徑 | 內容說明 |
| :--- | :--- | :--- |
| **專案說明** | [`README.md`](README.md) | 專案總覽、核心特色、快速啟動與建置說明 |
| **使用指南** | [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md) | 介面操作教學、混音面板、偏好設定與快捷鍵一覽 |
| **系統架構與技術文件** | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | 系統分層架構、技術棧、即時音訊與 60FPS 畫面同步管線 |
| **系統設計文件** | [`docs/SYSTEM_DESIGN.md`](docs/SYSTEM_DESIGN.md) | 資料模型、MusicXML 解析演算法、卷軸座標映射與動態主題設計 |
| **第三方元件授權聲明** | [`docs/THIRD_PARTY_NOTICES.md`](docs/THIRD_PARTY_NOTICES.md) | MeltySynth、NAudio 與 TimGM6mb.sf2 音源庫授權說明 |
| **版本更新紀錄** | [`CHANGELOG.md`](CHANGELOG.md) | 各版本功能與修正紀錄 |
| **開源授權條款** | [`LICENSE`](LICENSE) | MIT License 授權全文 |

---

## 🗂️ 專案目錄結構

```text
win-musicxmlplayer-01/
├── Assets/
│   ├── app.ico                        # 應用程式圖示
│   └── SoundFonts/
│       └── TimGM6mb.sf2               # 內建 General MIDI 真實樂器取樣音源庫
├── Controls/
│   ├── PianoKeyboardControl.cs        # 88 鍵動態鋼琴鍵盤自訂繪製控制項
│   └── PianoRollControl.cs            # 雙模式鋼琴卷軸 (水平時間軸 / 垂直瀑布流) 控制項
├── Models/
│   └── ScoreModels.cs                 # 樂譜、聲部軌道、音符事件、小節標記與樂器模型
├── Services/
│   ├── AudioEngine.cs                 # MeltySynth + NAudio 低延遲即時音訊合成引擎
│   ├── MusicXmlLoader.cs              # MusicXML / MXL 解析、反覆展開與演奏表情轉換器
│   ├── SoundFontManager.cs            # GM 128 樂器定義、聲部配器推斷與音源檔管理
│   └── ThemeManager.cs                # 顯示模式、視窗樣式、Hue 主題色與 DWM 整合管理
├── docs/
│   ├── USER_GUIDE.md                  # 使用指南
│   ├── ARCHITECTURE.md                # 系統架構與技術文件
│   ├── SYSTEM_DESIGN.md               # 系統設計文件
│   └── THIRD_PARTY_NOTICES.md         # 第三方元件與音源庫授權聲明
├── App.xaml / App.xaml.cs             # 應用程式進入點、全域資源與自動化驗證模式
├── MainWindow.xaml / .cs              # 主視窗介面、系統選單、混音與播放控制邏輯
├── SoundFontWindow.xaml / .cs         # 雙槽音源設定與免費音源下載建議視窗
├── PreferencesWindow.xaml / .cs       # 偏好設定視窗 (語言、顯示模式、視窗樣式、主題色)
├── AboutWindow.xaml / .cs             # 關於視窗 (軟體簡介、播放引擎與音源庫資訊)
├── WinMusicXmlPlayer.csproj           # .NET 8 WPF 專案定義檔
├── CHANGELOG.md                       # 版本更新紀錄
├── LICENSE                            # MIT License
└── README.md                          # 專案說明文件
```

---

## 🚀 快速開始

### 方式一：直接下載 Release 執行檔
1. 前往本專案的 **GitHub Releases** 頁面下載最新版本的 `WinMusicXmlPlayer-v1.0.0-win-x64.zip`。
2. 解壓縮後直接執行 `WinMusicXmlPlayer.exe`（已內建 `Assets/SoundFonts/TimGM6mb.sf2` 音源）。
3. 點擊左上角 **「開啟 MusicXML」**（或按 `Ctrl+O`，亦可直接將 `.musicxml` / `.mxl` 檔案拖曳至視窗中），再點擊底部 **「▶ 播放」** 即可聆聽演奏。

### 方式二：從原始碼編譯執行
- **環境需求**：Windows 10 / 11 (x64)、[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
# 1. 取得專案原始碼
git clone https://github.com/mark216tw/win-musicxmlplayer-01.git
cd win-musicxmlplayer-01

# 2. 編譯 Release 版本
dotnet build -c Release

# 3. 啟動應用程式
dotnet run -c Release
```

---

## ⌨️ 鍵盤快捷鍵一覽

| 快捷鍵 | 功能說明 |
| :--- | :--- |
| `Ctrl + O` | 開啟 MusicXML 樂譜檔案 (`.musicxml` / `.xml` / `.mxl`) |
| `Ctrl + M` | 開啟「音源設定與下載建議」視窗 |
| `Ctrl + P` / `Ctrl + ,` | 開啟「偏好設定」視窗 |
| `F1` | 開啟「關於」視窗 |
| `Ctrl + Q` / `Alt + F4` | 離開應用程式 |
| `Alt + F` | 展開「檔案」系統選單 |
| `Alt + S` | 展開「設定」系統選單 |
| `Alt + H` | 展開「說明」系統選單 |

---

## 📄 授權條款 (License)

本專案程式碼以 **MIT License** 授權發佈（Copyright (c) 2026 mark216tw `<mark216tw@gmail.com>`），詳見 [`LICENSE`](LICENSE)。  
隨附之第三方函式庫與 `TimGM6mb.sf2` 音源庫授權資訊請參閱 [`docs/THIRD_PARTY_NOTICES.md`](docs/THIRD_PARTY_NOTICES.md)。
