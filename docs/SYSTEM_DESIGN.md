# 系統設計文件 (System Design Document)

本文件詳述 **Windows MusicXML 真實音源播放器 (WinMusicXmlPlayer)** 的核心資料結構、樂理與時間軸轉換演算法、88 鍵鋼琴幾何座標映射公式，以及主題色彩空間設計。

---

## 1. 核心領域資料模型 (`Models/ScoreModels.cs`)

### 1.1 `MusicScore`（樂譜總體聚合根）
| 屬性名稱 | 型別 | 說明 |
| :--- | :--- | :--- |
| `Title` / `Composer` | `string` | 樂曲標題與作曲家名稱 |
| `Tracks` | `List<ScorePartTrack>` | 拆分後的可獨立控制聲部軌道清單 |
| `Notes` | `List<NoteEvent>` | 依 `StartTimeSeconds` 遞增排序的全曲音符事件清單 |
| `Measures` | `List<MeasureMarker>` | 展開反覆記號後的線性小節時間軸標記清單 |
| `TotalDurationSeconds` | `double` | 樂曲總時長（秒） |
| `MinMidiPitch` / `MaxMidiPitch` | `int` | 全曲最低與最高 MIDI 音高（用於水平卷軸自動計算最佳垂直音域範圍） |
| `MaxEndTimePrefix` | `double[]` | 前綴最大結束時間陣列，提供 `FindFirstNoteIndexForWindow` 與 `FindMeasureAt` 進行 $O(\log N)$ 二分搜尋 |

### 1.2 `ScorePartTrack`（聲部軌道模型）
實作 `INotifyPropertyChanged` 以支援 WPF 資料繫結：
- **識別與路由**：`TrackIndex`、`PartId`、`StaffNumber`、`VoiceFilter`、`MidiChannel` (`0..15`，避開 `9`)。
- **音色與混音狀態**：
  - `GmProgram` (`0..127`) 與 `InitialGmProgram`（記錄初始推斷音色，供「重置」按鈕還原）。
  - `Pan` (`0..127`，`64` 為正中央)：依據樂器家族自動配置交響樂團立體聲座位（例如第一小提琴偏左 `36`、第二小提琴偏右 `76`、中提琴 `82`、大提琴 `92`、鋼琴右手微偏右 `72`、左手微偏左 `54`）。
  - `Volume` (`0.0..1.5`，自動以 `0.05` 即 `5%` 為單位對齊)、`IsMuted`、`IsSolo`、`IsVisibleOnRoll`。
  - `AlphaBrushCache` (`SolidColorBrush[256]`)：預先凍結之 256 階透明度聲部色彩查表，供卷軸與鍵盤渲染零配置取用。
  - `CurrentActivity` (`0.0..1.0`)：即時發聲強度，採用快攻慢衰（Fast-Attack / Exponential-Decay）平滑演算法驅動 UI 動態條。

### 1.3 `NoteEvent`（雙時長音符事件模型）
為了同時兼顧「視覺卷軸上的譜面音符長度」與「真實聽覺上的斷奏/連奏發聲長度」，每個 `NoteEvent` 設計了雙時長欄位：
- `StartTimeSeconds` (`double`)：音符開始時間（秒）。
- `DurationSeconds` (`double`)：**視覺顯示時長**（供 `PianoRollControl` 繪製音符磚塊長度）。
- `AudioDurationSeconds` (`double`)：**實際音訊發聲時長**（供 `AudioEngine` 控制 `NoteOff` 時間）。
  - 斷奏 (`Staccato`)：`AudioDurationSeconds = DurationSeconds * 0.48`
  - 跳音 (`Staccatissimo`)：`AudioDurationSeconds = DurationSeconds * 0.28`
  - 圓滑線 (`Slur`) / 保持音 (`Tenuto`)：`AudioDurationSeconds = DurationSeconds * 0.99`
  - 一般音符：`AudioDurationSeconds = DurationSeconds * 0.92`

---

## 2. MusicXML 解析與時間軸編譯設計 (`Services/MusicXmlLoader.cs`)

### 2.1 反覆記號與一二房展開演算法
樂譜中的小節並非單純線性排列，可能包含反覆記號與不同房號結尾。`BuildPlaybackSequence` 狀態機設計如下：
1. 維護 `repeatStartIdx`（預設為第 0 小節，遇到 `RepeatForward` 時更新為該小節索引）。
2. 當掃描至第 `i` 小節時：
   - 若該小節標記為 `EndingNumber == 1`（一房），且目前已處於第二次反覆通過（`pass == 2`），則跳過一房區間直達二房（`EndingNumber == 2`）。
   - 若該小節帶有 `RepeatBackward` 且該反覆區間尚未反覆過，將 `i` 跳回 `repeatStartIdx` 並將 `pass` 設為 `2`。
3. 展開後的每個小節生成對應的 `MeasureMarker`，計算精確的絕對時間起點：
   $$\text{SecondsPerBeat} = \frac{60.0}{\text{TempoBpm}}$$
   $$\text{MeasureDurationSeconds} = \text{BeatDuration} \times \text{SecondsPerBeat}$$

### 2.2 連音線 (Tie) 合併機制
跨拍或跨小節的連音線（`<tie type="start"/>` 至 `<tie type="stop"/>`）若分別觸發 `NoteOn` 會產生不自然的重音敲擊。解析器在生成時間軸後，會針對同軌道、同音高且時間無縫相接的 `TieStart -> TieStop` 音符鏈進行合併，將後續音符的時長累加至首顆音符。

---

## 3. 88 鍵鋼琴幾何設計

### 3.1 88 鍵水平座標映射 (`PianoKeyboardControl.GetKeyHorizontalSpan`)
標準 88 鍵鋼琴涵蓋 MIDI Pitch `21` (`A0`) 至 `108` (`C8`)，共包含 **52 個白鍵** 與 **36 個黑鍵**：
- **白鍵寬度**：
  $$W_{\text{white}} = \frac{W_{\text{total}}}{52}$$
- **白鍵 X 座標**：設該音高前方的白鍵數量為 $k \in [0, 51]$，則 $X = k \times W_{\text{white}}$。
- **黑鍵寬度與非對稱偏移**：
  - 黑鍵寬度 $W_{\text{black}} = 0.62 \times W_{\text{white}}$。
  - 為重現真實平台鋼琴黑鍵群組（2 黑鍵組與 3 黑鍵組）的物理視覺比例，依音高類別 $pc = \text{pitch} \bmod 12$ 套用微調偏移率（`C#: 0.64`、`D#: 0.72`、`F#: 0.62`、`G#: 0.68`、`A#: 0.74`）。

### 3.2 同音連續敲擊回彈間隙設計 (Re-articulation Gap)
當樂曲出現同音反覆連打（例如前音結束時間等於後音開始時間 $t_{\text{end}, i} = t_{\text{start}, i+1}$）時，若直接以 $[t_{\text{start}}, t_{\text{end}}]$ 判斷琴鍵按壓，視覺上琴鍵會呈現持續按死狀態。  
系統於 `PianoKeyboardControl` 與 `PianoRollControl` 引入動態回彈間隙 $\Delta t_{\text{gap}}$：
$$\Delta t_{\text{gap}} = \text{clamp}(0.22 \times D_{\text{note}}, \, 0.035\text{s}, \, 0.068\text{s})$$
$$t_{\text{visualEnd}} = t_{\text{start}} + \max(0.025\text{s}, \, D_{\text{note}} - \Delta t_{\text{gap}})$$
確保在 60 FPS 畫面更新下，每次同音重擊前琴鍵必有 `2 ~ 4 幀` 彈回原位，呈現清晰的連續敲擊顆粒感。

---

## 4. 動態主題、HSL 色彩空間與原生 DWM 設計 (`Services/ThemeManager.cs`)

### 4.1 10 款精選主題色塊
- 主題色彩全面採用 **HSL (Hue, Saturation, Lightness)** 色彩空間建模：
  - 使用者可透過 10 個均勻調校的預設色塊選擇主題色，色相角度依序為：`199°`、`229°`、`262°`、`298°`、`332°`、`14°`、`45°`、`88°`、`128°`、`162°`。
  - 主強調色計算公式：$\text{ColorFromHue}(H) = \text{HSL}(H, \, S=0.88, \, L=0.58)$。
  - 按鈕背景與按壓態衍生色自動依 $H$ 計算不同明度（$L=0.44$、$L=0.50$、$L=0.36$），確保白色文字在任何預設色相下均具備良好對比度。

### 4.2 視窗樣式主題與 Windows DWM 整合
系統提供四種 `WindowStyleTheme`，同時控制 WPF 內部控制項資源與 Windows 11 原生 DWM 視窗屬性：
1. **`ModernFluent`（現代圓潤）**：`CardCornerRadius = 8`、DWM 標準圓角 (`DWMWCP_ROUND = 2`)、石板藍灰深淺色調。
2. **`StudioSharp`（專業俐落）**：`CardCornerRadius = 2`、DWM 直角邊框 (`DWMWCP_DONOTROUND = 1`)、高對比純黑/純白工作室配色。
3. **`ThemeTinted`（主題炫彩）**：`CardCornerRadius = 12`、將目前主題色相 $H$ 以低飽和度混入所有面板背景（例如深色模式 `HSL(H, 0.38, 0.09 ~ 0.16)`）並將原生視窗外框 (`DWMWA_BORDER_COLOR`) 染為目前主題色。
4. **`ConcertHall`（古典音樂廳）**：`CardCornerRadius = 6`、DWM 小圓角 (`DWMWCP_ROUNDSMALL = 3`)、黑檀木（深色）/ 羊皮紙（淺色）古典暖色調。

---

## 5. 記憶體映射音源與閒置資源管理 (`third_party/MeltySynth/src/SoundFontSampleData.cs`、`Services/MemoryOptimizer.cs`)

### 5.1 唯讀記憶體映射取樣資料 (Memory-Mapped Sample Data)
SoundFont 的 `smpl` 區塊可能高達數百 MB。為避免載入時將整份取樣資料複製到 managed heap，`SoundFontSampleData` 以 `MemoryMappedFile` 建立唯讀檢視，並透過 `AcquirePointer` 取得基底指標：
- `Oscillator.FillBlock_*` 以 `unsafe` 指標直接內插讀取樣本，僅在無法映射時才回退至 managed `short[]`。
- `SoundFont` 以原子引用計數管理生命週期；切換音源、離線 WAV 匯出與背景預覽可安全共用同一份映射，最後一個引用釋放時才關閉檔案映射。
- 啟動時先完成 UI 繪製，再於背景執行緒平行載入音源與上次樂譜，縮短首次可互動時間。

### 5.2 閒置記憶體整理與音訊輸出休眠
- `MemoryOptimizer.TrimIdleMemoryAsync()` 以冷卻時間與 `Interlocked` 防止重入，在啟動完成、視窗最小化、設定視窗關閉或音訊輸出閒置時，於 `ThreadPool` 執行 LOH 壓縮與 `SetProcessWorkingSetSizeEx` 工作集修剪；每次執行前後都會檢查是否正在播放，播放期間不介入。
- `AudioEngine` 在暫停、無作用中音符與預覽鍵、且連續 2 秒輸出峰值低於 `1e-5` 時，於背景安全處置 WASAPI 輸出；下一次 `Play()` 或 `PreviewNoteOn()` 會重新初始化裝置。
- `MainWindow` 僅在播放、時間變動或聲部動態表仍有活動時保留 `CompositionTarget.Rendering` 訂閱，其餘時間解除，使閒置 CPU 與記憶體佔用趨近於零。
