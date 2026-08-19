# Emulator gaps

What this terminal emulator does **not** implement, and the symptom each omission produces.

The point of this file is triage. When a program renders wrongly, check here first: most of these
fail *silently* — the sequence is consumed and ignored, so there is no error, no log line, and no
crash, just output that looks subtly wrong. Knowing a gap exists turns a debugging session into a
lookup.

Audited against the source on 2026-08-19. Each entry says how it was verified so the audit can be
repeated rather than trusted.

---

## Not implemented

### DCS — device control strings (no parsing at all)

`EscapeSequenceParser` holds a `_dcs` StringBuilder and an event marked
`[Obsolete("DCS parsing is not implemented yet")]`. Nothing dispatches it.

**Consequences**

| Feature | Uses DCS | Symptom |
|---|---|---|
| Sixel graphics | `DCS P q` | Images do not render. Depending on where parsing stops, the payload may print as garbage text. |
| DECRQSS (request status string) | `DCS $ q` | A program asking "what is the current SGR / scroll region?" gets no reply and may wait, or assume a default. |
| XTGETTCAP | `DCS + q` | Terminfo capability queries go unanswered. **tmux itself uses this** to discover what the outer terminal supports. |
| Kitty graphics protocol | `APC`/DCS | Images do not render. |

**Likely to bite:** image output (`timg`, `chafa`, notebook previews). Programs that *query* mostly
degrade to a safe default rather than hanging, but a program that blocks on a DECRQSS reply will
appear to freeze.

---

### DEC 2026 — synchronized output

Not in `TerminalMode`; `CSI ? 2026 h/l` is consumed and ignored.

Modern TUIs wrap a frame in begin/end synchronized update so the terminal shows the completed
frame rather than a partial one. Supported by tmux 3.4+, kitty, WezTerm, iTerm2, Windows Terminal
and Ghostty.

**Symptom:** tearing and flicker during full-screen repaints — a half-drawn frame visible for one
paint. Nothing breaks; it just looks worse than in a terminal that honours it. Most noticeable on
programs that redraw the whole screen at speed.

---

### DECRQM — mode query

`CSI ? Pm $ p` is not answered.

**Symptom:** a program that probes whether a mode is available before using it gets silence and
falls back to its conservative path. So a feature the emulator *does* support may go unused
because the program could not confirm it. Silent under-use rather than breakage.

---

### Extended keyboard protocols

Neither xterm's `modifyOtherKeys` (`CSI > 4 ; Pm m`) nor the kitty keyboard protocol
(`CSI > 1 u`) is implemented.

**Symptom:** key combinations that require disambiguation do not reach the program.
<kbd>Ctrl</kbd>+<kbd>I</kbd> is indistinguishable from <kbd>Tab</kbd>, <kbd>Ctrl</kbd>+<kbd>M</kbd>
from <kbd>Enter</kbd>, and most <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+key combinations are lost.
Editors that rely on them — Neovim, Helix — will appear to ignore those bindings.

---

### XTVERSION — terminal identification

`CSI > 0 q` is not answered.

**Symptom:** programs that gate features on terminal identity treat this as an unknown terminal
and choose their most conservative rendering. Combined with the DECRQM gap, a program has two ways
to ask "can you do X?" and gets an answer to neither.

---

### Underline styles and colour

`UnderlineStyle { Curly, Dotted, Dashed }` is **declared in `Common/Types.cs` and referenced
nowhere**. `AttributeData` exposes only a boolean `IsUnderline()`, with no underline colour
(SGR 58/59).

**Symptom:** `CSI 4:3 m` (curly) renders as a plain underline, and the underline colour is
ignored. LSP diagnostics in Neovim lose the distinction between a warning squiggle and an error
squiggle — both become the same flat underline in the text colour.

This is the cheapest gap to close and the most likely to matter for a coding TUI.

---

### Other absences

- **OSC 133** (semantic prompt marking) — no command-boundary tracking, so "jump to previous
  prompt" and per-command decoration cannot be implemented on top.
- **OSC 1337 / iTerm2 proprietary** — inline images, badges.
- **OSC 9 progress** — the Windows Terminal / ConEmu taskbar progress indicator.
- **Blink is stored but not driven** — `IsBlink()` is recorded per cell; making it actually blink
  is a renderer concern.

---

## Implemented (so the gaps stay in proportion)

Verified present in `TerminalMode`, `OscCommand`, `InputHandler` and `Charsets`:

- **Colour:** 16, bright (90–97 / 100–107), 256 (`38;5`), and **24-bit true colour**
  (`38;2;R;G;B`), with palette 256/257 as the default-fg/bg sentinels.
- **Mouse:** click (9), normal (1000), button-event (1002), any-event (1003), plus SGR (1006),
  UTF-8 (1005), urxvt (1015) and **pixel (1016)** encodings. Focus events (1004).
- **Buffers:** alternate screen in all three forms (47, 1047, 1049).
- **Modes:** bracketed paste (2004), application cursor keys (1) and keypad (66), origin (6),
  auto-wrap (7), reverse wraparound (45), insert (4), reverse video (5), cursor visibility (25),
  meta/alt-sends-escape (1036/1039), 8-bit input (1034).
- **OSC:** 0/1/2 titles, 4 palette, 7 working directory, **8 hyperlinks (stored per cell)**,
  10/11/12 colours, 52 clipboard, 104/110/111 resets.
- **Charsets:** DEC Special Graphics (line drawing), UK, US ASCII.
- **Layout:** scroll regions, tab stops, wide characters via `Wcwidth`, and
  **reflow of wrapped lines on width change**.

---

## Fork additions

Features added here that upstream does not have. Listed because a bug in one of these is *ours*,
not upstream's.

| Addition | Why | Risk to watch |
|---|---|---|
| `TerminalByteFeed` | Stateful UTF-8 decoding across chunk boundaries, so a multi-byte sequence split by a read boundary is not corrupted. | Decoder state is per-feed; sharing one feed across two producers interleaves their bytes. |
| `TerminalSerializer` | Renders current state as replayable escape sequences — screen-sized rather than history-sized, which is what detach/reattach needs. | Reproduces what the terminal *shows*, not the program's own model. A program tracking its cursor independently may still need a repaint trigger. |
| `BufferCell.Hyperlink` | OSC 8 was an event only, so links vanished from any redraw. | Adds a reference per cell. |
| Reflow on resize | Width changes clipped or broke wrapped lines. | Cursor placement across a reflow is heuristic at wrap boundaries; the alternate buffer is deliberately excluded. |

---

## Repeating this audit

```bash
# CSI commands
grep -E "case CsiCommand" src/XTerm.NET/InputHandler.cs

# Modes
grep -E "^\s+[A-Za-z0-9]+ = [0-9]+," src/XTerm.NET/Common/TerminalMode.cs

# OSC
grep -E "^\s+[A-Za-z0-9]+ = [0-9]+," src/XTerm.NET/Common/OscCommand.cs

# Is DCS still unimplemented?
grep -n "not implemented" src/XTerm.NET/Parser/EscapeSequenceParser.cs

# Is UnderlineStyle still dead?
grep -rn "UnderlineStyle" src/XTerm.NET/ --include=*.cs | grep -v Types.cs
```
