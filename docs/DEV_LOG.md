# 開發日誌 (Development Log)

本文件按時間軸（由新至舊）詳細記錄 **Windows MusicXML 真實音源播放器 (WinMusicXmlPlayer)** 的功能新增、演算法調校、問題排查與架構演進過程。  
（若需查看對外發布的版本摘要，請參閱根目錄之 [`CHANGELOG.md`](../CHANGELOG.md)）

---

## 📅 2026-10-29 — v1.2.1 MuseScore 鋼琴悶／無聲修正與 MeltySynth 原始碼內嵌

### 🎯 問題與根因
使用者回報載入 `MuseScore_General.sf2` 與 `MS_Basic.sf2` 後鋼琴沒有聲音，後續回報音色很悶、很小聲。解析 SF2 後確認兩者都包含 bank 0 / patch 0 的 `Grand Piano`，且取樣資料、鍵域與 loop 範圍皆正常。進一步比對生成器後確認：

- `mf`／`f`（velocity 75–107）的 preset zone 設定了負向 modulation envelope → filter cutoff（`op11 = -2350`）。
- 鋼琴 instrument 全域初始截止頻率為 `op8 = 6237`（約 **300 Hz**），mod-envelope sustain 為 100%（`op29 = 1000`）；其他音源多為 12–20 kHz。
- 初版以 100 Hz 保護避免穩態 cutoff 掉到約 77 Hz，但實際仍停在 300 Hz，泛音幾乎被濾除，因此聽感仍悶。

### 🛠 修正
1. 將 MeltySynth 2.4.1（commit `1ad7ba0`）原始碼內嵌至 `third_party/MeltySynth/`，移除原本的 `MeltySynth.dll` 參考。
2. `SynthesizerSettings` 新增 `MinimumFilterCutoffHz` 與 `FilterCutoffFloorMaxProgram`，預設關閉以維持原行為。
3. `Voice.cs` 在取得初始 cutoff 及每次 modulation 計算後套用下限；僅當 channel 非打擊通道且 patch 在指定範圍內時生效，並移除初版 100 Hz 特例。
4. `AudioEngine` 以 `SoundFont.Info.BankName`（含 `MuseScore`）或檔名偵測 MuseScore 系列，對 GM 鋼琴 0–5 套用 **4 kHz** 下限；即時播放與離線 WAV 匯出共用同一設定。
5. 新增 `--verify-soundfont [path]` 驗證模式，輸出 velocity 60–125 的峰值與 velocity 96 頻譜重心，並檢查力度與單調性。
6. 更新第三方授權聲明、架構文件與版本資訊。

### ✅ 驗證
- `MuseScore_General` velocity 96：頻譜重心 `569 Hz`、峰值 `-17.8 dBFS`。
- `MS_Basic` velocity 96：頻譜重心 `566 Hz`、峰值 `-20.0 dBFS`。
- 對照 `TimGM6mb`：`546 Hz`；`YDP-GrandPiano`：`712 Hz`；`GeneralUser-GS`：`473 Hz`。
- `dotnet build -c Release`：0 警告、0 錯誤；`--verify-all` 與 `--verify-soundfont` 全數通過。

### 📂 異動檔案
- [`third_party/MeltySynth/src/SynthesizerSettings.cs`](../third_party/MeltySynth/src/SynthesizerSettings.cs)
- [`third_party/MeltySynth/src/Synthesizer.cs`](../third_party/MeltySynth/src/Synthesizer.cs)
- [`third_party/MeltySynth/src/Voice.cs`](../third_party/MeltySynth/src/Voice.cs)
- [`Services/AudioEngine.cs`](../Services/AudioEngine.cs)
- [`App.xaml.cs`](../App.xaml.cs)
- [`WinMusicXmlPlayer.csproj`](../WinMusicXmlPlayer.csproj)
- [`docs/THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)
- [`docs/ARCHITECTURE.md`](ARCHITECTURE.md)
- [`docs/DEV_LOG.md`](DEV_LOG.md)
- [`CHANGELOG.md`](../CHANGELOG.md)
- [`README.md`](../README.md)
- [`docs/USER_GUIDE.md`](USER_GUIDE.md)

---

## 📅 2026-10-28 (深夜) — v1.2.0 演奏表情與音樂性提升（A1–A4 + B5–B7）

### 🎯 修改動機與背景
在影音同步與卷軸效能穩定後，進一步提升播放的音樂性。既有程式已具備力度、漸強弱、重音、裝飾音、踏板與聲部平衡，但仍缺少琶音方向、呼吸記號、完整鋼琴自動踏板、樂器別空間感、人性化、延遲顫音與可調力度曲線。

### ✨ 新增與修改功能
1. **琶音方向與速度**：
   - 解析 `<arpeggiate direction="down">`，依方向排序和弦音。
   - 滾奏速度依 BPM 與和弦時值自動調整（12–40ms），首尾音微調力度。
2. **呼吸記號與 Cesura**：
   - 解析 `<breath-mark>`、`<caesura>`，換算為 0.06–0.45 秒停頓。
   - 時間映射區分「音符開始」與「音符結束」，避免把停頓算進帶記號音符的時長。
   - `MeasureMarker.BeatLineTimes` 保存實際拍線時間，卷軸拍線同步修正。
3. **自動鋼琴踏板**：
   - 逐鋼琴聲部判斷；無明確踏板記號時，以低音或音高集合變化自動換踏。
   - 單次最長 2.5 秒、換踏前 30ms 清音，並合併過於密集的換踏點。
4. **樂器別空間感**：
   - `AudioEngine` 依 GM 家族調整 reverb／chorus send；全域滑桿仍作為 master，預設殘響 30%、合唱 15%。
5. **人性化演奏**：
   - 固定雜湊產生可重現的微時序（±12ms）、力度（±5）與鋼琴和弦滾奏（每音最多 6ms）。
   - 預設 30%，偏好設定提供「推薦 30」按鈕。
6. **延遲顫音（CC1）**：
   - 弦樂／管樂長音建立合併後的顫音視窗，延遲 0.18–0.30 秒漸入、句尾漸出。
   - `AudioEngine` 新增 CC1 自動化套用；Load／Seek／Pause／Stop 時重置，避免殘留顫音。
7. **力度曲線與動態範圍**：
   - 新增柔和／線性／強勁曲線與 50–150% 動態範圍，套用於 `NormalizeVelocity()`；預設「柔和」與 70%，提升 `p`／`pp` 弱音樂段的可聽度。
8. **偏好設定與重新載入**：
   - 新增「演奏表情 (Performance)」卡片。
   - 變更需重新載入的設定後，自動保留播放位置、播放狀態與 A-B 循環並重新載入樂曲。
9. **啟動自動載入上次樂譜**：
   - `ThemeManager` 新增 `LastOpenedScorePath`，成功載入樂譜後記錄完整路徑；清除最近紀錄時一併清除。
   - `MainWindow_Loaded` 在音源與卷軸初始化後檢查檔案是否存在，存在才自動載入並停在 `00:00`；缺檔時維持空白畫面，解析失敗僅顯示 Toast。
   - `--verify-all`／`--capture-ui` 以 `AutoLoadLastScore = false` 建立視窗，避免自動載入影響自我測試與截圖。
10. **音符區單擊播放／暫停**：
    - `PianoRollControl` 以 4px 移動門檻區分單擊與拖曳；水平模式僅音符區（不含頂部尺標與左側音高列）可切換，垂直模式整區可切換。
    - 以 `ClickCount` 抑制雙擊的第二次切換；拖曳仍觸發 Scrubbing。
    - `MainWindow` 共用 `TogglePlayPause()`，Scrub 暫停時同步更新播放按鈕。
11. **主音量預設與記憶**：
    - `ThemeManager` 新增 `MasterVolume`，預設 `100%`，記住上次非靜音音量；靜音不寫入。
    - `AudioEngine` 預設主音量同步改為 `1.0`，啟動時套用已儲存值。

### ✅ 驗證
- Debug／Release 建置 0 錯誤；`--verify-all` 全數 7 首曲目、演奏表情自我測試與 UI 自我測試 `ALL PASS`。
- 新增合成 XML 測試：琶音下行、breath/caesura 停頓、自動踏板、人性化可重現性與偏移上限、顫音自動化、力度曲線。

### 📂 異動檔案
- [`Models/PerformanceOptions.cs`](../Models/PerformanceOptions.cs)
- [`Models/ScoreModels.cs`](../Models/ScoreModels.cs)
- [`Services/ThemeManager.cs`](../Services/ThemeManager.cs)
- [`Services/MusicXmlLoader.cs`](../Services/MusicXmlLoader.cs)
- [`Services/AudioEngine.cs`](../Services/AudioEngine.cs)
- [`MainWindow.xaml.cs`](../MainWindow.xaml.cs)
- [`PreferencesWindow.xaml`](../PreferencesWindow.xaml)
- [`PreferencesWindow.xaml.cs`](../PreferencesWindow.xaml.cs)
- [`Controls/PianoRollControl.cs`](../Controls/PianoRollControl.cs)
- [`App.xaml.cs`](../App.xaml.cs)
- [`CHANGELOG.md`](../CHANGELOG.md)
- [`docs/USER_GUIDE.md`](USER_GUIDE.md)
- [`docs/ARCHITECTURE.md`](ARCHITECTURE.md)

---

## 📅 2026-10-28 (晚間後續) — 以裝置實際播放位置校正影音同步與卷軸渲染快取

### 🎯 修改動機與背景
延續稍早的 A/V Sync 優化，使用者實測回饋卷軸其實是「跑得比較快」，將偏移調為 `0 ms` 後剛好。進一步量測發現 WASAPI 實際輸出緩衝約 `54–55 ms`，比原本硬編碼的 `45 ms` 多，導致舊版固定補償加時間軸提前量後，畫面整體偏快。

### ✨ 新增與修改功能
1. **實測輸出緩衝延遲**：
   - `AudioEngine` 追蹤已寫入音訊位元組，並以 `IWavePosition.GetPosition()` 取得裝置實際播放位置，計算「已寫入 − 已播放」的緩衝音訊量，取代固定 `45 ms` 假設。
   - 每個音訊回呼更新時鐘錨點時同步更新實測緩衝量；`GetPosition()` 不可用時才退回估計值。
2. **水平卷軸每幀成本優化**：
   - 快取小節標籤字串與 `FormattedText`、`RectangleGeometry` 裁剪區與 A/B 標記文字，避免每幀重新配置。
   - 垂直瀑布流一併套用小節標籤快取。
3. **預設偏移與範圍調整**：
   - `VisualSyncOffsetMs` 新安裝預設改為 `0 ms`（既有使用者已儲存的值保留，不自動遷移）。
   - 滑桿範圍由 `-100 ms` ～ `+100 ms` 擴大為 `-200 ms` ～ `+200 ms`，「推薦」按鈕同步改為 `0 ms`。
   - 文件同步更新 `CHANGELOG.md`、`docs/USER_GUIDE.md`。

### ✅ 驗證
- Debug／Release 建置 0 錯誤；`dotnet WinMusicXmlPlayer.dll --verify-all` 全數 7 首曲目與 UI 自我測試 `ALL PASS`。
- 直接播放《月光》量測：實測輸出緩衝穩定約 `54–55 ms`，UI 時間與實測播放位置差值等於目前偏移值。

### 📂 異動檔案
- [`Services/AudioEngine.cs`](../Services/AudioEngine.cs)
- [`Services/ThemeManager.cs`](../Services/ThemeManager.cs)
- [`MainWindow.xaml`](../MainWindow.xaml)
- [`MainWindow.xaml.cs`](../MainWindow.xaml.cs)
- [`App.xaml.cs`](../App.xaml.cs)
- [`Controls/PianoRollControl.cs`](../Controls/PianoRollControl.cs)
- [`Controls/PianoKeyboardControl.cs`](../Controls/PianoKeyboardControl.cs)
- [`PreferencesWindow.xaml`](../PreferencesWindow.xaml)
- [`PreferencesWindow.xaml.cs`](../PreferencesWindow.xaml.cs)
- [`CHANGELOG.md`](../CHANGELOG.md)
- [`docs/USER_GUIDE.md`](USER_GUIDE.md)

---

## 📅 2026-10-28 (傍晚) — 影音極致同步（A/V Sync）優化：高精度秒錶錨點內插時鐘與自訂畫面提前補償

### 🎯 修改動機與背景
使用者回饋於演奏時：「**感覺聲音比較快聽到，音符卷軸、鋼琴彈奏的畫面的反應比較慢**」。  
經深入排查音訊合成驅動管線與 WPF 圖形渲染管線，定位出造成「聲快影慢」的兩大深層根因：
1. **WASAPI 區塊輪詢抖動與離散跳躍**：
   - 舊版 `MainWindow.xaml.cs` 的 `OnCompositionTargetRendering` 直接讀取 `_audioEngine.CurrentTimeSeconds`。
   - 但 `CurrentTimeSeconds` 僅在 NAudio 音訊背景執行緒向 WASAPI 寫入緩衝區時跳躍（WASAPI 共享模式每次要求 35ms～55ms 音訊資料）。在兩次回呼之間的時間，`CurrentTimeSeconds` 是完全凍結的；導致 WPF 渲染每隔 2～3 幀（60Hz 下約 33ms～50ms）畫面時間完全沒變而略過更新，產生約 20FPS 的階梯式卡頓與反應遲滯感。
2. **音訊輸出與視訊顯示管線的物理延遲不對稱**：
   - **音訊端**：WASAPI 將已合成的 PCM 緩衝區送入音效卡 DAC，聲波經由耳機／喇叭傳播至耳膜，延遲極短（一般約 20ms～35ms），且人類大腦聽覺反應時間約 140ms。
   - **視訊端**：WPF 在 UI 執行緒建構 DrawingContext 視覺樹後，需經由 Render 執行緒、Direct3D 命令緩衝區提交至 Windows 桌面視窗管理員（DWM），並在下一個垂直同步（V-Sync）週期才由螢幕物理掃描輸出（在 60Hz 螢幕下單是 DWM 與顯示管線延遲即達 25ms～45ms），且人類視覺感知反應較聽覺慢約 50ms。
   - 在缺乏前向管線補償（Visual Lead Compensation）的情況下，當耳朵聽見音符強拍敲擊時，畫面上的音符塊往往還在播放紅線右側數像素處尚未抵達，琴鍵也尚未亮起下壓，造成明顯的「聲音先出來、畫面慢半拍」。

### ✨ 新增與修改功能
1. **高精度秒錶錨點時鐘內插機制（Stopwatch Anchor Interpolated Clock）**：
   - 在 `AudioEngine` 引入高精度 QPC 時鐘機制（`_clockLock`、`_clockAnchorMediaSec`、`_clockAnchorTicks`、`_clockAnchorValid`、`_outputLatencySec`）。
   - 每次 WASAPI 完成音訊合成並排入緩衝區時，精確記錄當前媒體時間與 `Stopwatch.GetTimestamp()` 錨點。
   - 實作 `GetVisualTimeSeconds(double visualLeadSeconds)`：每幀畫面更新時，透過高精度秒錶經過時間與當前播放速率無鎖平滑內插，扣除 WASAPI 輸出緩衝延遲並加上畫面提前量，使卷軸與鍵盤達到真正的 60/120/144 FPS 絲滑連續推進，徹底消除跳幀。
   - 加入調度抖動容許裕度（`_clockAnchorMediaSec + 0.080 * speed`），避免音訊背景執行緒受 Windows 排程微小抖動時凍結畫面幀率。
   - 實作 `GetAudibleTimeSeconds()`：暫停時精確回退至使用者實際「聽覺點」，避免暫停時畫面跳到已送入音效卡緩衝區但尚未被聽見的未來位置。
2. **畫面提前量補償與雙向影音同步**：
   - 針對 Windows WPF / DWM 顯示管線延遲，預設配置 `+35 ms`（0.035 秒）之視訊提前量。
   - 音符到達卷軸播放線的時刻、88 鍵鋼琴按壓閃光動畫的瞬間，與音訊波形離開喇叭物理發聲的時間點精準重合。
3. **喜好設定新增「影音同步微調（A/V Sync Offset）」卡片與即時預覽**：
   - 於 `ThemeManager` 與 `UserPreferencesData` 新增 `VisualSyncOffsetMs` 設定（範圍 `-100 ms` ～ `+100 ms`，步進 5ms，預設推薦 `+35 ms`），支援自動儲存於 `user_preferences.json`。
   - 於 `PreferencesWindow`（偏好設定視窗）新增第 6 項獨立設定卡片「影音同步微調 (A/V Sync)」，附帶直覺滑桿與「推薦 (+35ms)」一鍵重設按鈕。
   - 支援**播放中即時動態生效**（無需重新啟動程式或暫停），使用者可邊播放音樂邊微調滑桿，完美適應不同耳機（如藍牙耳機有額外無線延遲）、外接 USB DAC 或不同螢幕更新率。
   - `PreferencesWindow` 增加 `ScrollViewer` 捲動保護與尺寸微調支援，避免在筆記型電腦高 DPI 縮放環境截斷設定控制項。
4. **全樂譜自動化驗證**：
   - 執行 `dotnet WinMusicXmlPlayer.dll --verify-all`，`staffs/` 下全數 7 首古典大作（《月光奏鳴曲》、《命運交響曲》、《四季：春》、《K.496 三重奏》、《土耳其進行曲》、《小夜曲 K.525》、《K.545》）與 UI 自我測試全數通過（`ALL PASS`）。

### 📂 異動檔案
- [`Services/AudioEngine.cs`](../Services/AudioEngine.cs)
- [`Services/ThemeManager.cs`](../Services/ThemeManager.cs)
- [`MainWindow.xaml.cs`](../MainWindow.xaml.cs)
- [`PreferencesWindow.xaml`](../PreferencesWindow.xaml)
- [`PreferencesWindow.xaml.cs`](../PreferencesWindow.xaml.cs)
- [`docs/DEV_LOG.md`](DEV_LOG.md)
- [`CHANGELOG.md`](../CHANGELOG.md)

---

## 📅 2026-10-28 (下午) — 古典鋼琴曲、小提琴協奏曲與室內樂演奏表現力深度優化

### 🎯 修改動機與背景
針對常聽的曲目類型：**鋼琴獨奏曲**（莫札特《K.545》、《土耳其進行曲 K.331》、貝多芬《月光奏鳴曲》）、**小提琴協奏曲／弦樂合奏**（韋瓦第《四季：春》、莫札特《小夜曲 K.525》）與**鋼琴與提琴室內樂**（莫札特《G 大調鋼琴三重奏 K.496》）進行 `staffs/` 全曲目深度檢視與實測，解決以下影響音樂性的關鍵問題：
1. 樂譜出現突強記號（`sf` / `sfp` / `fz`）後，持續性力度變數被覆寫，導致後續整段弱音（`p`）卡死在強音。
2. 《小夜曲 K.525》中多達 84 處的 `<tremolo type="single">2</tremolo>`（十六分音符定速連弓）僅被切換為 GM 44 合成器震音，未展開為實際的十六分音符反覆弓音。
3. 含有 `<fermata>`（延長記號）的小節採用固定倍率拉長，導致延長拍點前後的音符與下一小節拍線錯位。
4. 完全無 `<dynamics>` 標記的古典鋼琴譜（如《K.545》）所有音符力度齊平，左手阿爾貝蒂低音（Alberti Bass）過大聲；而無 `<pedal>` 標籤的《月光奏鳴曲》缺乏跨拍和聲踏板延音。
5. 《貝多芬第五號交響曲》中的 `Bassoon`（低音管）因包含 `"basso"` 字串被誤判為 `Contrabass`（低音提琴）。

### ✨ 新增與修改功能
1. **突強記號（`sf` / `sfp` / `fp` / `fz` / `sfz`）瞬時重音與衰減曲線**：
   - 將突強記號與持續性基底力度（`currentDynamicVel`）分離：突強僅作用於當下音符（含同時間點的和弦音），下一個音符立即自動恢復原本的基底力度（如 `p`）。
   - 若為 `sfp` 或 `fp` 且聲部屬於弦樂／管樂（如《K.496 三重奏》小提琴聲部），除了起音給予強音觸鍵外，自動於起音後 $60\text{ms} \sim 240\text{ms}$ 注入快速衰減至弱音的 CC11 Expression 自動化曲線。
2. **文字力度與漸強標記（`<words>cresc.</words>` / `dim.`）及 `<offset>` 偏移支援**：
   - 解析 `<direction>` 內的 `<offset>` 標籤，精準計算拍子中途的力度與表情變化時間點。
   - 支援以 `<words>` 書寫的 `cresc.`、`dim.`、`decresc.` 與括號力度 `(f)` / `(p)`（如《小夜曲 K.525》），自動觸發漸強／漸弱斜坡。
3. **弦樂定速斜線反覆弓音（Measured Bowed Tremolo）自動展開**：
   - 讀取 `<tremolo>` 數值：當斜線數為 `1`（八分音符細分，0.5 拍）或 `2`（十六分音符細分，0.25 拍）時，透過 `ExpandMeasuredTremoloNote()` 自動將單一音符拆分為對應數量的快速連弓子音符，保留原本的小提琴／中提琴／大提琴音色（GM 40/41/42）並賦予強弱弓微律動。
   - 僅當斜線數 $\ge 3$（未定速碎弓 Unmeasured Tremolo）時，才切換為 `GM Program 44 (Tremolo Strings)`。
4. **延長記號（`<fermata>`）分段拍點時間映射（Piecewise Beat-to-Time Mapping）**：
   - 實作 `MapBeatToOffsetSec()`：在 Fermata 拍點之前的拍子維持原速（$1.0\times$），僅針對 Fermata 所在拍位進行 $1.60\times$ 延展，確保小節結束時間與所有聲部音符 100% 對齊，同時支援休止符上的 `<fermata>`。
5. **古典曲目拍點微律動（Metric Accent）、鋼琴聲部層次與智慧和聲延音踏板**：
   - 新增 `ComputeMusicalVoicingDelta()`：依拍號自動賦予小節第一拍強拍（$+4$）、4/4 拍第三拍次強拍（$+2$）與弱拍／十六分音符內拍（$-1 \sim -4$）的自然呼吸律動。
   - 針對鋼琴曲：微幅托出右手主旋律（Staff 1，$+4$），並將左手快速分解和弦伴奏（Staff 2 Alberti Bass）的拍頭根音與內聲部反覆音柔化（$-3 \sim -8$），消除機械感。
   - 新增 `BuildImplicitHarmonicPedalIntervals()`：針對未標記 `<pedal>` 的慢板鋼琴曲（如《月光奏鳴曲》），自動依左手低音（Staff 2）和聲變換切分延音踏板區間（單段上限 3.2 秒，換和聲前預留 $25\text{ms}$ 清音間隙）。
6. **小提琴協奏曲與鋼琴室內樂的主奏／伴奏空間平衡（Solo vs. Tutti Balance）**：
   - 當樂譜同時包含主奏小提琴（`Violino principale` / `Solo`）與協奏弦樂群時，自動給予主奏小提琴聚焦增益（$+7$）；並解析譜面 `<words>Solo</words>` 與 `<words>Tutti</words>` 標記（如《四季：春》），在獨奏樂段自動讓伴奏弦樂群退讓（$-6$）。
   - 在鋼琴與提琴重奏（如《K.496 鋼琴三重奏》）中，自動微幅增益小提琴（$+4$）與大提琴（$+5$），避免被鋼琴雙手密集音群掩蓋。
7. **樂器識別修正與驗證路徑支援**：
   - 調整 `SoundFontManager.InferInstrumentAndPan()` 判斷順序，將管樂（含 `Bassoon` / `fagotto` / `bsn`）移至 `Contrabass`（含 `"basso"`）之前，並新增 `定音鼓 (Timpani)` 等中文識別。
   - 更新 `App.RunAutomatedVerification()` 自動優先檢索 `staffs/` 目錄下的 `.musicxml` 樂譜。

### 📂 異動檔案
- [`Services/MusicXmlLoader.cs`](../Services/MusicXmlLoader.cs)
- [`Services/SoundFontManager.cs`](../Services/SoundFontManager.cs)
- [`App.xaml.cs`](../App.xaml.cs)
- [`CHANGELOG.md`](../CHANGELOG.md)

---

## 📅 2026-10-28 (上午) — SoundFont 載入相容性、音源路由透明化與基礎演奏表情

### 🎯 修改動機與背景
部分大型管弦樂 `.sf2`（如 `AegeanSymphonicOrchestra-SND.sf2`）因含有 `EndLoop = 0` 的單次不循環取樣，在舊版 `MeltySynth 2.1.0` 會拋出越界例外；同時使用者希望能清楚辨識每個聲部目前走的是「🎹 獨立鋼琴音源」還是「🎻 通用音源」，並能調整廳堂殘響（Reverb）與合唱（Chorus）空間感。

### ✨ 新增與修改功能
1. **升級 `MeltySynth` 2.1.0 → 2.4.1 與無效音源防護**：
   - 解決合法 `.sf2` 中 `EndLoop = 0` 被誤判為越界的載入失敗問題。
   - 新增 `SoundFontManager.TryValidateSoundFontFile()` 與 `ValidateLoadedSoundFont()`，快速掃描 `smpl` 與 `phdr` 區塊，攔截全靜音（全 0）或損毀的 `.sf2` 檔案並自動回退至可用音源。
2. **聲部卡片音源路由顯示與專屬 Preset 名稱對應**：
   - 聲部卡片新增「音源」狀態列，明確標示「🎹 獨立鋼琴」或「🎻 通用音源」。
   - 各聲部的「GM 音色」下拉選單改為讀取該聲部實際路由音源的 Bank 0 Preset 名稱；若該音源未提供指定 Program，以灰色標示並於播放時智慧 Fallback。
   - 音源選擇對話框統一記憶上次瀏覽的資料夾路徑。
3. **基礎演奏法與空間效果器（Reverb / Chorus）**：
   - 解析 MusicXML `<pedal>` 延音踏板標記、`<wedge>` 漸強漸弱符號（轉為 CC11 自動化）、`slur`/`tenuto` 連音重疊，以及 `pizz.` / `arco` 與 `<technical>` 標記。
   - 於「偏好設定」視窗新增「殘響 Reverb（CC91）」與「合唱 Chorus（CC93）」即時調整滑桿。

### 📂 異動檔案
- [`third_party/MeltySynth/src/Voice.cs`](../third_party/MeltySynth/src/Voice.cs)
- [`Models/ScoreModels.cs`](../Models/ScoreModels.cs)
- [`Services/AudioEngine.cs`](../Services/AudioEngine.cs)
- [`Services/MusicXmlLoader.cs`](../Services/MusicXmlLoader.cs)
- [`Services/SoundFontManager.cs`](../Services/SoundFontManager.cs)
- [`Services/ThemeManager.cs`](../Services/ThemeManager.cs)
- [`MainWindow.xaml`](../MainWindow.xaml) / [`MainWindow.xaml.cs`](../MainWindow.xaml.cs)
- [`PreferencesWindow.xaml`](../PreferencesWindow.xaml) / [`PreferencesWindow.xaml.cs`](../PreferencesWindow.xaml.cs)
- [`SoundFontWindow.xaml.cs`](../SoundFontWindow.xaml.cs)

---

## 📅 2026-10-26 (晚間) — v1.1.0 零配置 60FPS 渲染優化、互動試聽、A-B 循環與 WAV 匯出

### 🎯 修改動機與背景
針對長篇交響樂（如《貝多芬第五號交響曲》超過 11,000 個音符、627 小節）進行效能剖析，消除每幀渲染的物件配置（GC 壓力）與音訊執行緒鎖競爭，並加入進階練琴與賞析輔助功能（A–B 段循環、琴鍵試聽、小節跳轉、離線匯出 WAV）。

### ✨ 新增與修改功能
1. **互動與賞析功能新增**：
   - **88 鍵鋼琴鍵盤點擊與滑奏（Glissando）即時試聽**：滑鼠左鍵點擊或橫向滑過底部琴鍵即可透過當前鋼琴音源即時發聲。
   - **A–B 區間循環播放（Loop A–B）**：支援於卷軸右鍵或點擊底部 `🔁 A-B 循環` 按鈕設定起訖區間並無縫循環。
   - **小節號直接輸入跳轉（`Ctrl+G`）**：點擊底部「小節：X / Y」徽章可原位輸入小節號跳轉。
   - **音符懸停資訊浮窗（Note Hover Info Badge）**：滑鼠指向卷軸音符時即時顯示聲部、音高、MIDI 鍵號、小節、力度與奏法。
   - **離線極速匯出 WAV（`Ctrl+E`）**：使用獨立雙槽合成器實例以約 $50\times$ 即時速度渲染輸出 44.1kHz / 16-bit 立體聲 `.wav`。
   - **最近開啟的樂譜清單**與**音量 5% 步進／雙擊重置／`M` 鍵主音量靜音**。
2. **底層效能與音訊無鎖化優化**：
   - 建立單調遞增前綴陣列 `MaxEndTimePrefix` 與 `FindFirstNoteIndexForWindow()` 二分搜尋，將卷軸與鍵盤視窗掃描複雜度由 $O(N)$ 降至 $O(\log N + K)$。
   - 快取 `FormattedText`、凍結（Freeze）所有 `Brush` / `Pen` 資源，達成 60FPS 零配置渲染。
   - 實作 `UpdateTrackVolumesOnly()` 輕量 CC7 更新，拖拉音量滑桿不再重設 Program Change 或切斷發聲殘響。

### 📂 異動檔案
- [`Controls/PianoRollControl.cs`](../Controls/PianoRollControl.cs)
- [`Controls/PianoKeyboardControl.cs`](../Controls/PianoKeyboardControl.cs)
- [`Services/AudioEngine.cs`](../Services/AudioEngine.cs)
- [`Services/MusicXmlLoader.cs`](../Services/MusicXmlLoader.cs)
- [`Services/ThemeManager.cs`](../Services/ThemeManager.cs)
- [`Models/ScoreModels.cs`](../Models/ScoreModels.cs)
- [`MainWindow.xaml`](../MainWindow.xaml) / [`MainWindow.xaml.cs`](../MainWindow.xaml.cs)
- [`docs/SOUNDFONT_GUIDE.md`](SOUNDFONT_GUIDE.md)

---

## 📅 2026-10-26 (下午) — SoundFont 2 雙槽音源分流合成與下載指南視窗

### 🎯 修改動機與背景
單一通用 GM 音源（`.sf2`）往往難以同時兼顧「平台鋼琴的厚實共鳴」與「管弦樂／小提琴的細膩音色」。因此設計「雙槽音源分流架構」，讓使用者能同時掛載一顆專用鋼琴 `.sf2` 與一顆通用管弦樂 `.sf2`。

### ✨ 新增與修改功能
1. **雙槽 `MeltySynth` 合成器同步混音**：
   - 於 `AudioEngine` 內建 `_synth`（通用樂器）與 `_pianoSynth`（鋼琴專用）雙合成器實例。
   - 當載入鋼琴專用音源時，凡屬鋼琴家族（GM Program 0–5）的聲部自動路由至 `_pianoSynth`，小提琴、大提琴、管樂等聲部則由 `_synth` 渲染，並於音訊緩衝區進行無失真混音與軟限幅（`SoftClip`）。
2. **音源設定與推薦下載對話框（`Ctrl+M`）**：
   - 新增 [`SoundFontWindow`](../SoundFontWindow.xaml)，支援分別指定或清除通用音源與鋼琴專用音源，並內建開源高品質 `.sf2` 推薦清單。
3. **播放鍵智慧開啟檔案**：
   - 未載入樂譜時直接點擊「▶ 播放」，自動彈出檔案選擇對話框並於選取後立即開始演奏。

### 📂 異動檔案
- [`Services/AudioEngine.cs`](../Services/AudioEngine.cs)
- [`Services/SoundFontManager.cs`](../Services/SoundFontManager.cs)
- [`SoundFontWindow.xaml`](../SoundFontWindow.xaml) / [`SoundFontWindow.xaml.cs`](../SoundFontWindow.xaml.cs)
- [`MainWindow.xaml.cs`](../MainWindow.xaml.cs)

---

## 📅 2026-10-26 (中午) — v1.0.0 初始版本發布

### 🎯 修改動機與背景
打造專為 Windows 設計的現代化 MusicXML 真實音源播放器，結合多聲部拆解、雙模式動態鋼琴卷軸與真實取樣波表合成。

### ✨ 核心架構建立
1. **MusicXML 解析引擎（`MusicXmlLoader`）**：
   - 支援 `.musicxml`、`.xml`、`.mxl`（ZIP 壓縮格式）與 JSON 封裝樂譜。
   - 支援反覆記號（Repeat）、一二房（1st/2nd Ending）、裝飾音（Grace Notes、顫音 Trill、波音 Mordent、迴音 Turn）、琶音（Arpeggiate）、連結線（Tie）與多譜表／多聲部智慧拆軌（如《月光奏鳴曲》自動拆分為右手主旋律、右手三連音伴奏、左手低音三軌）。
2. **波表合成音訊引擎（`AudioEngine` + `NAudio` WASAPI）**：
   - 內建 `TimGM6mb.sf2` 128 種 GM 真實樂器取樣，支援各聲部獨立靜音（`M`）、獨奏（`S`）、音色切換、立體聲相位（Pan）與即時動態音量表。
3. **雙模式鋼琴卷軸與 88 鍵動態鍵盤**：
   - 支援「水平時間軸」與「垂直瀑布流」一鍵切換、滑鼠左鍵拖曳時間軸（Scrubbing）與滾輪縮放。
4. **主題與偏好設定系統（`ThemeManager`）**：
   - 支援系統／深色／淺色模式、4 種視窗風格（現代圓潤、專業俐落、主題炫彩、古典音樂廳）與自訂主題色相（Hue）。
