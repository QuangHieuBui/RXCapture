# Video working copy, encode on Save: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stopping a recording opens the video in the editor at once (no conversion); MP4/GIF is written only when the user saves.

**Architecture:** The recorder keeps writing AVI + WAV. They are moved into the library as `<id>.avi` + `<id>.wav` (a "working copy"). The player already reads AVI through Media Foundation; the sound comes from the sibling WAV through a PCM-reader abstraction shared by playback and trim. Save / Save As runs the existing `Mp4Writer.Convert` (moved out of `VideoRecorder.Finish`), copies the MP4 to the chosen path, turns the item into an MP4 item and deletes the AVI + WAV.

**Tech Stack:** C# 5 (.NET Framework 4.0 `csc`, no `?.`, no string interpolation), WinForms, Media Foundation P/Invoke, winmm. Tests are the project's own harness (`--selftest`, `--interact`, `--filetest`, `--videotest`); a new check is `Check("name", delegate { ...; return null /* or error text */; })` in `src/SelfTest.cs`.

**Spec:** `docs/superpowers/specs/2026-10-16-video-working-copy-design.md`

## Global Constraints
- Build: `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` (close a running RXCapture first); tests: `bin\RXCapture.exe --selftest <log>` etc.; a log line starting `FAIL` or exit code 1 is a failure.
- No change to the capture loop, `AudioRecorder`, recorder-bar UI, fps / quality settings, `GifWriter`, `Mp4Writer.Reader`.
- Library file names: `<id>.avi`, `<id>.wav` (same id), later `<id>.mp4`. The WAV format is `AudioRecorder` (44-byte header `AudioRecorder.WavHeader`, `AudioRecorder.Rate`, `AudioRecorder.Channels`, 16-bit).
- The Save dialog for a working copy offers MP4 and GIF only; sound forces MP4 (as today). The Settings format (mp4/gif/avi) only picks the default entry; "avi" maps to MP4.
- Save of an MP4 or GIF item keeps today's behaviour (`CopyVideoTo`).
- New user-visible text goes through `Loc.T("English text")` with a Vietnamese entry in `src/Translations.cs`.
- Do not commit or push; the last task asks the user first.

## Review Focus
1. Recording without sound: no `.wav` in the library, the working copy plays silently, Save gives an MP4 without an audio track (Task 2, Task 4).
2. A working copy whose `.wav` was deleted by hand: still plays (silent), still lists, Save works without sound (Task 1, Task 4).
3. Save while the video is playing or paused (the player holds the AVI open): the file must be released before the AVI is deleted; cancelling the dialog changes nothing (Task 5).
4. Conversion fails (corrupt AVI, disk full, no encoder): AVI and WAV remain, the item is unchanged, an error message is shown, no half-written MP4 is left (Task 4).
5. An orphan `<id>.wav` (its AVI deleted or recycled) must not appear as a library item or break `List()`; deleting or pruning an item removes its `.wav` too, never sends it to the Recycle Bin on its own (Task 2).

---

### Task 1: PCM reader abstraction and WAV-backed audio

**Files:**
- Modify: `src/Video/Mp4Writer.cs` (`IAudioSource` neighbourhood ~181-355, `Trim` ~363, `Convert` ~69)
- Modify: `src/Video/AudioPlayer.cs` (`TryOpen`, `Feed`, `Play`)
- Test: `src/SelfTest.cs`

**Interfaces:**
- Produces:
  - `public interface Mp4Writer.IPcmReader : IAudioSource, IDisposable { int Rate { get; } int Channels { get; } int BlockAlign { get; } void Seek(TimeSpan t); }`
  - `Mp4Writer.AudioReader` implements `IPcmReader` (no other change).
  - `public sealed class Mp4Writer.WavReader : IPcmReader` replaces the private `WavSource` (same 100 ms chunks, constructor `WavReader(string wavPath)`); `Seek(t)` positions the stream at `AudioRecorder.WavHeader + frame * BlockAlign` and restarts the chunk clock at that frame.
  - `public static Mp4Writer.IPcmReader Mp4Writer.OpenAudio(string path)`: for a `.avi` path returns a `WavReader` over `Path.ChangeExtension(path, ".wav")` when that file exists, else null; for any other path returns `AudioReader.TryOpen(path)`.
- Consumes: nothing from other tasks.

- [ ] **Step 1: Write the failing check** in `src/SelfTest.cs` next to the sound-mux check: `Check("working copy sound: an AVI with a sibling WAV opens as PCM, seeks, a plain AVI has none, an MP4 still uses its AAC track", ...)`. Assert: `OpenAudio(avi)` not null with `Rate == AudioRecorder.Rate`; reading to the end gives 2 s +- 100 ms; after `Seek(1 s)` the first chunk has `time >= 0.9 s`; with the WAV deleted `OpenAudio(avi)` is null; for a converted MP4 `OpenAudio(mp4)` is not null.
- [ ] **Step 2: Run** build + `--selftest`. Expected: compile error (`OpenAudio` / `IPcmReader` undefined).
- [ ] **Step 3: Implement** the interface, `WavReader`, `OpenAudio`; `TrimmedAudio` takes `IPcmReader`; `Trim` uses `OpenAudio(src)`; `AudioPlayer` uses `Mp4Writer.OpenAudio` and `IPcmReader`.
- [ ] **Step 4: Run** build + `--selftest`. Expected: new check and existing sound checks PASS.

### Task 2: Library stores working copies

**Files:**
- Modify: `src/Output/LibraryStore.cs` (`AddVideo` ~80, `List` ~137, `DeleteFiles` ~285)
- Test: `src/SelfTest.cs`

**Interfaces:**
- Produces:
  - `LibraryStore.AddVideo(string videoFile, Bitmap firstFrame, int seconds, string wavFile = null)`: moves the video to `<id>.<ext>`, and `wavFile` (when it exists) to `<id>.wav`.
  - `public static string LibraryStore.SoundFile(LibItem it)`: `Path.ChangeExtension(it.File, ".wav")` when `it.Ext == "avi"` and that file exists, else null.
  - `public static void LibraryStore.ReplaceVideoFile(LibItem it, string newFile)`: `newFile` (already in the library folder, e.g. `<id>.mp4`) becomes `it.File` / `it.Ext`; the previous video file and `<id>.wav` are deleted; `Raise()`.

- [ ] **Step 1: Write failing checks**: working copy has `.avi`, `SoundFile` exists, listed once; an orphan `<otherid>.wav` is not listed; `Delete(item, true)` leaves no `<id>.*`; `ReplaceVideoFile` gives `Ext == "mp4"` and removes avi + wav; `AddVideo` without wav gives `SoundFile == null`.
- [ ] **Step 2: Run** build + `--selftest`. Expected: compile error.
- [ ] **Step 3: Implement.** `List` excludes `.wav` when picking the video file; `DeleteFiles` treats `.wav` as not main (always deleted).
- [ ] **Step 4: Run.** Expected: PASS incl. existing library checks.

### Task 3: Recording stops without converting

**Files:**
- Modify: `src/Video/VideoRecorder.cs` (`Finish`), `src/Translations.cs`, `src/Editor/EditorForm.cs` (`ShowNewVideo`)
- Test: `src/InteractTest.cs`

**Interfaces:**
- Produces: `internal static long VideoRecorder.LastFinishMs`; `RunFfmpeg` becomes `internal`; `Finish` no longer converts.
- Consumes: `AddVideo(..., wavFile)` (Task 2).

- [ ] **Step 1: Change expectations** in `InteractTest.Run` (format loop) and `SoundCheck`: new item has `Ext == "avi"`, file > 5000 bytes, `SoundFile` non-null in `SoundCheck`; `LastFinishMs < 1500`; sound read via `Mp4Writer.OpenAudio(v.File)`.
- [ ] **Step 2: Run** `--interact`. Expected: FAIL.
- [ ] **Step 3: Implement** `Finish` (AddVideo with wav, ShowEditor, ShowNewVideo, balloon); `ShowNewVideo` status hint for AVI items; translations.
- [ ] **Step 4: Run.** Expected: PASS.

### Task 4: Encoding on Save (`VideoExport`)

**Files:** Create `src/Video/VideoExport.cs`; Test `src/SelfTest.cs`.

**Interfaces:**
- Produces (`public static class VideoExport`): `bool WriteMp4(string avi, string wavOrNull, string mp4, out bool soundDropped)`; `int FpsOf(string file)`; `bool SaveWorkingCopy(LibItem it, string dest, string format, out bool soundDropped)` (`"mp4"`: library `<id>.mp4` then copy to dest, `ReplaceVideoFile`, `SetExport`; `"gif"`: `AviToGif` to dest, `SetExport`, AVI and WAV kept; any failure leaves everything untouched, no partial output).

- [ ] **Step 1: Failing checks:** MP4 with sound (valid, duration within a frame, audio present, avi+wav gone, item MP4, ExportPath); failed conversion changes nothing; no-sound; WAV deleted by hand; GIF export keeps the AVI.
- [ ] **Step 2: Run.** Expected: compile error.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.

### Task 5: Editor Save / Save As use the working copy

**Files:** Modify `src/Editor/EditorForm.cs` (`SaveVideoAs`, `SaveQuick`), `src/Translations.cs`; tests in `src/SelfTest.cs` / `src/InteractTest.cs`.

**Interfaces:** Produces `internal bool EditorForm.SaveVideoTo(string dest, string format)` (dialog-free part: stops the player, runs `VideoExport.SaveWorkingCopy` on a worker thread inside a `BusyForm`, reopens the item, status hint; false + message on failure).

- [ ] **Step 1: Failing checks:** Save As on a recording makes an MP4 item and the editor keeps showing it (AVI deleted without a sharing error); a forced failure leaves AVI + WAV; trim of a working copy keeps the range and its sound.
- [ ] **Step 2: Run.** Expected: compile error.
- [ ] **Step 3: Implement.** Working-copy dialog filter MP4 / GIF, default from settings (sound forces MP4); `SaveQuick` sends a working copy to `SaveVideoAs`.
- [ ] **Step 4: Run.** Expected: PASS.

### Task 6: Regression, timing, notes
- [ ] Run all four suites; report any failure other than the known flaky ones.
- [ ] Measure stop-to-editor time (`LastFinishMs`), target under 500 ms.
- [ ] README note; spec tweak (status-bar hint instead of a title change).
- [ ] Ask the user before committing.
