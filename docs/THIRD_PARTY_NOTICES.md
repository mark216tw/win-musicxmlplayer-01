# 第三方元件與音源庫授權聲明 (Third-Party Notices)

**Windows MusicXML 真實音源播放器 (WinMusicXmlPlayer)** 主程式原始碼採用 [MIT License](../LICENSE) 授權。本專案使用了以下優秀的開源函式庫與音源庫，特此致謝並列出其授權條款：

---

## 1. MeltySynth
- **用途**：SoundFont 2 (`.sf2`) 即時波表軟體合成器引擎
- **作者**：Sinshu (Nobuaki Tanaka)
- **專案網址**：https://github.com/sinshu/meltysynth
- **授權條款**：MIT License
- **內嵌版本**：2.4.1（commit `1ad7ba0b9f7324461b08aeca75a220ab1253de56`）
- **原始碼位置**：`third_party/MeltySynth/src/`
- **本專案修改**：於 `SynthesizerSettings` 新增 `MinimumFilterCutoffHz` 與 `FilterCutoffFloorMaxProgram`，並在 `Voice.cs` 對指定 GM program 套用濾波器截止下限。此機制用於修正 `MuseScore_General.sf2` / `MuseScore_General_Full.sf2` / `MS_Basic.sf2` 鋼琴約 300 Hz 的過低初始截止頻率；未啟用下限時行為與原始碼一致。

```text
MIT License
Copyright (c) 2021 Nobuaki Tanaka
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software...
```

---

## 2. NAudio (`NAudio.Core`, `NAudio.Wasapi`, `NAudio.WinMM`)
- **用途**：Windows WASAPI 與 WaveOut 低延遲音訊串流輸出
- **作者**：Mark Heath & Contributors
- **專案網址**：https://github.com/naudio/NAudio
- **授權條款**：MIT License

```text
MIT License
Copyright (c) 2020 Mark Heath
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software...
```

---

## 3. TimGM6mb.sf2 (General MIDI SoundFont)
- **檔案路徑**：`Assets/SoundFonts/TimGM6mb.sf2`
- **用途**：預設 General MIDI 128 種標準樂器取樣音源庫
- **作者**：Tim Brechbill (源自 MuseScore 1.x / Debian `timgm6mb-soundfont` 套件)
- **授權條款**：GNU General Public License v2.0 (GPL v2)
- **說明**：`TimGM6mb.sf2` 為獨立之資料/音源資產檔案，於執行期由播放器動態載入（亦可由使用者自行替換為任意 `.sf2` 音源檔），依據 GPL v2 條款隨附散佈。
