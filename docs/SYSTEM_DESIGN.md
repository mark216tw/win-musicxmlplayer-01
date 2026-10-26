# 系統設計文件 (System Design Document)

本文件詳述 **Windows MusicXML 真實音源播放器 (WinMusicXmlPlayer)** 的核心資料結構、樂理與時間軸轉換演算法、88 鍵與雙模式卷軸幾何座標映射公式，以及主題色彩空間設計。

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

### 1.2 `ScorePartTrack`（聲部軌道模型）
實作 `INotifyPropertyChanged` 以支援 WPF 資料繫結：
- **識別與路由**：`TrackIndex`、`PartId`、`StaffNumber`、`VoiceFilter`、`MidiChannel` (`0..15`，避開 `9`)。
- **音色與混音狀態**：
  - `GmProgram` (`0..127`) 與 `InitialGmProgram`（記錄初始推斷音色，供「重置」按鈕還原）。
  - `Pan` (`0..127`，`64` 為正中央)：依據樂器家族自動配置交響樂團立體聲座位（例如第一小提琴偏左 `36`、第二小提琴偏右 `76`、中提琴 `82`、大提琴 `92`、鋼琴右手微偏右 `72`、左手微偏左 `54`）。
  - `Volume` (`0.0..1.5`)、`IsMuted`、`IsSolo`、`IsVisibleOnRoll`。
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

## 3. 88 鍵鋼琴與雙模式卷軸幾何設計

### 3.1 88 鍵水平座標映射 (`PianoKeyboardControl.GetKeyHorizontalSpan`)
標準 88 鍵鋼琴涵蓋 MIDI Pitch `21` (`A0`) 至 `108` (`C8`)，共包含 **52 個白鍵** 與 **36 個黑鍵**：
- **白鍵寬度**：
  $$W_{\text{white}} = \frac{W_{\text{total}}}{52}$$
- **白鍵 X 座標**：設該音高前方的白鍵數量為 $k \in [0, 51]$，則 $X = k \times W_{\text{white}}$。
- **黑鍵寬度與非對稱偏移**：
  - 黑鍵寬度 $W_{\text{black}} = 0.62 \times W_{\text{white}}$。
  - 為重現真實平台鋼琴黑鍵群組（2 黑鍵組與 3 黑鍵組）的物理視覺比例，依音高類別 $pc = \text{pitch} \bmod 12$ 套用微調偏移率（`C#: 0.64`、`D#: 0.72`、`F#: 0.62`、`G#: 0.68`、`A#: 0.74`）。
- **垂直瀑布流對齊**：`PianoRollControl` 在 `VerticalFalling` 模式下直接呼叫 `PianoKeyboardControl.GetKeyHorizontalSpan(pitch, width)`，確保瀑布流落下的每一顆音符與下方 88 鍵琴鍵達到 100% 像素級對齊。

### 3.2 同音連續敲擊回彈間隙設計 (Re-articulation Gap)
當樂曲出現同音反覆連打（例如前音結束時間等於後音開始時間 $t_{\text{end}, i} = t_{\text{start}, i+1}$）時，若直接以 $[t_{\text{start}}, t_{\text{end}}]$ 判斷琴鍵按壓，視覺上琴鍵會呈現持續按死狀態。  
系統於 `PianoKeyboardControl` 與 `PianoRollControl` 引入動態回彈間隙 $\Delta t_{\text{gap}}$：
$$\Delta t_{\text{gap}} = \text{clamp}(0.22 \times D_{\text{note}}, \, 0.035\text{s}, \, 0.068\text{s})$$
$$t_{\text{visualEnd}} = t_{\text{start}} + \max(0.025\text{s}, \, D_{\text{note}} - \Delta t_{\text{gap}})$$
確保在 60 FPS 畫面更新下，每次同音重擊前琴鍵必有 `2 ~ 4 幀` 彈回原位，呈現清晰的連續敲擊顆粒感。

---

## 4. 動態主題、HSL 色彩空間與原生 DWM 設計 (`Services/ThemeManager.cs`)

### 4.1 單一 Hue 彩色滑桿與預設色塊雙向連動
- 主題色彩全面採用 **HSL (Hue, Saturation, Lightness)** 色彩空間建模：
  - 使用者可透過 6 個預設色塊（對應特定 Hue 角度：`199°`、`14°`、`42°`、`156°`、`262°`、`332°`）或 `0°–360°` 滑桿選擇任意色相角 $H \in [0, 360]$。
  - 主強調色計算公式：$\text{ColorFromHue}(H) = \text{HSL}(H, \, S=0.88, \, L=0.58)$。
  - 按鈕背景與按壓態衍生色自動依 $H$ 計算不同明度（$L=0.44$、$L=0.50$、$L=0.36$），確保白色文字在任何自訂色相下均具備良好對比度。

### 4.2 視窗樣式主題與 Windows DWM 整合
系統提供四種 `WindowStyleTheme`，同時控制 WPF 內部控制項資源與 Windows 11 原生 DWM 視窗屬性：
1. **`ModernFluent`（現代圓潤）**：`CardCornerRadius = 8`、DWM 標準圓角 (`DWMWCP_ROUND = 2`)、石板藍灰深淺色調。
2. **`StudioSharp`（專業俐落）**：`CardCornerRadius = 2`、DWM 直角邊框 (`DWMWCP_DONOTROUND = 1`)、高對比純黑/純白工作室配色。
3. **`ThemeTinted`（主題炫彩）**：`CardCornerRadius = 12`、將目前主題色相 $H$ 以低飽和度混入所有面板背景（例如深色模式 `HSL(H, 0.38, 0.09 ~ 0.16)`）並將原生視窗外框 (`DWMWA_BORDER_COLOR`) 染為目前主題色。
4. **`ConcertHall`（古典音樂廳）**：`CardCornerRadius = 6`、DWM 小圓角 (`DWMWCP_ROUNDSMALL = 3`)、黑檀木（深色）/ 羊皮紙（淺色）古典暖色調。
