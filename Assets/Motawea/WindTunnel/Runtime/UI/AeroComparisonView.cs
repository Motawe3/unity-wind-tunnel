using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Motawea.WindTunnel.UI
{
    /// <summary>
    /// Modal overlay for comparing two exported test sessions: pick a result file on
    /// each side, see whether they are the same experiment on the same basis, then the
    /// metric-by-metric delta and a verdict that refuses to call a winner inside the
    /// uncertainty on the two numbers being differenced.
    ///
    /// The layout is built here rather than in a UXML asset so the same element can be
    /// hosted by the runtime HUD's UIDocument and by an editor window. Everything that
    /// needs a pseudo-class (hover, active, disabled) or reaches into Unity's internal
    /// scroller and dropdown parts lives in the package stylesheet
    /// <c>Resources/WindTunnel/AeroComparison.uss</c>, which is loaded here — so the modal
    /// is themed by itself and does not depend on the host project's USS.
    ///
    /// A and B each carry an identity colour (teal / violet) through the picker titles,
    /// the selected rows, the column markers and the verdict. Green, amber and red are
    /// reserved for verdicts, so no colour ever means both "side B" and "better".
    /// </summary>
    public class AeroComparisonView : VisualElement
    {
        const string StyleSheetPath = "WindTunnel/AeroComparison";

        // Test-cell palette, matching the runtime console.
        static readonly Color Ink = new Color(0.055f, 0.067f, 0.086f, 0.97f);
        static readonly Color Panel = new Color(0.078f, 0.094f, 0.118f, 1f);
        static readonly Color Line = new Color(0.14f, 0.16f, 0.196f);
        static readonly Color Text = new Color(0.914f, 0.925f, 0.941f);
        static readonly Color Muted = new Color(0.55f, 0.59f, 0.64f);
        static readonly Color Accent = new Color(0.2f, 0.76f, 0.82f);
        static readonly Color Good = new Color(0.369f, 0.788f, 0.384f);
        static readonly Color Warn = new Color(0.902f, 0.667f, 0.235f);
        static readonly Color Bad = new Color(0.847f, 0.337f, 0.29f);

        // Side identities: A = the console accent, B = violet (unused by any verdict).
        static readonly Color SideA = new Color(0.2f, 0.76f, 0.82f);
        static readonly Color SideB = new Color(0.655f, 0.545f, 0.98f);

        readonly VisualElement _listA, _listB, _resultHost;
        readonly Label _pathLabel, _statusLabel, _exportNote;
        readonly Button _exportButton;
        readonly DropdownField _testA, _testB;

        List<AeroReportFile> _files = new List<AeroReportFile>();
        AeroReportFile _selectedA, _selectedB;
        int _testIndexA, _testIndexB = -1;
        string _directory;

        /// <summary>Raised when the user dismisses the modal.</summary>
        public event Action Closed;

        public AeroComparisonView(string directory = null)
        {
            _directory = string.IsNullOrEmpty(directory) ? AeroSessionArchive.DefaultDirectory : directory;

            // Buttons, list rows, dropdowns and scrollers are styled entirely by this
            // sheet — they carry no inline styling, because inline would outrank the
            // hover and active rules. If it ever fails to load, say so once rather
            // than leaving someone puzzling over an unstyled dialog.
            var sheet = Resources.Load<StyleSheet>(StyleSheetPath);
            if (sheet != null) styleSheets.Add(sheet);
            else Debug.LogWarning($"Wind Tunnel: missing Resources/{StyleSheetPath}.uss — the comparison modal will render unstyled.");

            AddToClassList("aero-cmp-root");
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
            style.backgroundColor = new Color(0f, 0f, 0f, 0.65f);
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            // Swallow clicks that miss the dialog so they never reach the scene.
            RegisterCallback<PointerDownEvent>(e => e.StopPropagation());

            var dialog = new VisualElement { name = "compare-dialog" };
            dialog.AddToClassList("aero-cmp-dialog");
            dialog.style.width = Length.Percent(86);
            dialog.style.maxWidth = 1180;
            dialog.style.height = Length.Percent(88);
            dialog.style.backgroundColor = Ink;
            Border(dialog, Line, 1, 4);
            dialog.style.paddingLeft = dialog.style.paddingRight = 18;
            dialog.style.paddingTop = 14;
            dialog.style.paddingBottom = 14;
            Add(dialog);

            // ---- header --------------------------------------------------------
            var header = Row();
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;

            var titleBox = new VisualElement();
            titleBox.Add(Title("COMPARE TEST RESULTS"));
            _pathLabel = Caption(_directory);
            titleBox.Add(_pathLabel);
            header.Add(titleBox);

            var headerButtons = Row();
            headerButtons.Add(Btn("OPEN FOLDER", () => AeroSessionArchive.OpenDirectory(_directory),
                                  "Show the exported results in the file browser"));
            headerButtons.Add(Btn("REFRESH", Reload, "Re-read the report folder"));
            headerButtons.Add(Btn("CLOSE", () => Closed?.Invoke()));
            header.Add(headerButtons);
            dialog.Add(header);

            dialog.Add(Hairline());

            // ---- file pickers --------------------------------------------------
            var pickers = Row();
            pickers.style.marginTop = 12;

            var columnA = PickerColumn("A", "RESULT A", SideA, out _listA, out _testA);
            var columnB = PickerColumn("B", "RESULT B", SideB, out _listB, out _testB);
            columnB.style.marginLeft = 14;
            pickers.Add(columnA);
            pickers.Add(columnB);
            dialog.Add(pickers);

            _testA.RegisterValueChangedCallback(_ =>
            {
                _testIndexA = Mathf.Max(0, _testA.index);
                _testIndexB = -1; // re-match against the new procedure
                Rebuild();
            });
            _testB.RegisterValueChangedCallback(_ =>
            {
                _testIndexB = _testB.index;
                Rebuild();
            });

            _statusLabel = Caption("Select a result on each side.");
            _statusLabel.style.marginTop = 10;
            dialog.Add(_statusLabel);

            // ---- results -------------------------------------------------------
            var scroll = new ScrollView { style = { flexGrow = 1, marginTop = 6 } };
            _resultHost = new VisualElement();
            scroll.Add(_resultHost);
            dialog.Add(scroll);

            // ---- footer: take the comparison out of the tool ---------------------
            var footer = Row();
            footer.style.justifyContent = Justify.SpaceBetween;
            footer.style.alignItems = Align.Center;
            footer.style.marginTop = 10;
            footer.style.paddingTop = 10;
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = Line;

            _exportNote = Caption("");
            _exportNote.style.flexShrink = 1;
            _exportNote.style.overflow = Overflow.Hidden;
            _exportNote.style.textOverflow = TextOverflow.Ellipsis;
            // Ellipsis only applies to a non-wrapping label; without this the note wraps
            // to a second line and pushes itself below the dialog's bottom edge.
            _exportNote.style.whiteSpace = WhiteSpace.NoWrap;
            _exportNote.style.marginRight = 10;
            footer.Add(_exportNote);

            _exportButton = Btn("EXPORT COMPARISON", () => ExportHtml(),
                                "Write this comparison — audit, deltas and verdict — as a self-contained HTML page");
            _exportButton.SetEnabled(false);
            footer.Add(_exportButton);
            dialog.Add(footer);

            Reload();
        }

        /// <summary>Points the browser at another folder of exported results.</summary>
        public void SetDirectory(string directory)
        {
            _directory = directory;
            _pathLabel.text = directory;
            Reload();
        }

        /// <summary>Re-reads the report folder, keeping the current selection when it survives.</summary>
        public void Reload()
        {
            string keepA = _selectedA?.path, keepB = _selectedB?.path;
            _files = AeroSessionArchive.List(_directory);
            _selectedA = _files.Find(f => f.path == keepA);
            _selectedB = _files.Find(f => f.path == keepB);
            _pathLabel.text = $"{_directory} · {_files.Count} result file(s)";
            FillList(_listA, true);
            FillList(_listB, false);
            RefreshTestDropdowns();
            Rebuild();
        }

        /// <summary>
        /// Selects both sides by file path — for deep-linking a comparison (and for
        /// tests). Returns false if either path is not among the listed results.
        /// </summary>
        public bool SelectPaths(string pathA, string pathB)
        {
            var a = _files.Find(f => f.path == pathA);
            var b = _files.Find(f => f.path == pathB);
            if (a == null || b == null) return false;

            _selectedA = a;
            _selectedB = b;
            _testIndexA = 0;
            _testIndexB = -1;
            FillList(_listA, true);
            FillList(_listB, false);
            RefreshTestDropdowns();
            Rebuild();
            return true;
        }

        /// <summary>The comparison currently on screen, or null when nothing is selected.</summary>
        public AeroComparisonReport CurrentReport { get; private set; }

        /// <summary>
        /// Writes the comparison on screen as a self-contained HTML page next to the
        /// results it came from. Returns the path, or null when there is nothing to
        /// write. A blocked comparison still exports — the audit explaining *why* two
        /// runs cannot be differenced is often the thing worth sending to someone.
        /// </summary>
        public string ExportHtml()
        {
            if (CurrentReport == null || !CurrentReport.Valid)
            {
                SetExportNote("NOTHING TO EXPORT — SELECT A RESULT ON EACH SIDE", Warn);
                return null;
            }

            try
            {
                string path = AeroComparisonExporter.ExportTo(CurrentReport, _directory);
                SetExportNote($"SAVED → {System.IO.Path.GetFileName(path)}", Good);
                RevealInFileBrowser(path);
                return path;
            }
            catch (Exception e)
            {
                SetExportNote($"EXPORT FAILED — {e.Message}", Bad);
                Debug.LogWarning($"Wind Tunnel: comparison export failed — {e}");
                return null;
            }
        }

        /// <summary>
        /// Opens the containing folder with the exported file selected where the platform
        /// allows it (Windows Explorer); elsewhere just opens the folder. A failure here
        /// must never look like a failed export — the file is already on disk.
        /// </summary>
        static void RevealInFileBrowser(string path)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(path);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{full}\"");
#else
                Application.OpenURL("file://" + System.IO.Path.GetDirectoryName(full));
#endif
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Wind Tunnel: exported fine, but could not open the folder — {e.Message}");
            }
        }

        void SetExportNote(string text, Color color)
        {
            if (_exportNote == null) return;
            _exportNote.text = text;
            _exportNote.style.color = color;
        }

        // ------------------------------------------------------------------ pickers

        VisualElement PickerColumn(string letter, string title, Color side,
                                   out VisualElement list, out DropdownField testPicker)
        {
            var column = new VisualElement { style = { flexGrow = 1, flexBasis = 0 } };

            var titleRow = Row();
            titleRow.style.alignItems = Align.Center;
            titleRow.style.marginBottom = 8;
            titleRow.Add(Badge(letter, side));
            var label = new Label(title);
            label.style.fontSize = 12;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.letterSpacing = 3;
            label.style.color = side;
            titleRow.Add(label);
            column.Add(titleRow);

            var box = new VisualElement { style = { height = 152 } };
            Border(box, Line, 1, 3);
            box.style.backgroundColor = Panel;
            // The selected row's identity stripe reads as part of the box edge unless
            // the box owns a matching inner margin.
            box.style.paddingTop = box.style.paddingBottom = 2;
            var scroll = new ScrollView { style = { flexGrow = 1 } };
            list = new VisualElement();
            scroll.Add(list);
            box.Add(scroll);
            column.Add(box);

            testPicker = new DropdownField { choices = new List<string>() };
            testPicker.AddToClassList("aero-cmp-dropdown");
            testPicker.SetEnabled(false);
            column.Add(testPicker);
            return column;
        }

        void FillList(VisualElement list, bool sideA)
        {
            list.Clear();
            if (_files.Count == 0)
            {
                var empty = Caption("No exported results in this folder.\nRun a test, then EXPORT REPORT.");
                empty.style.whiteSpace = WhiteSpace.Normal;
                empty.style.paddingLeft = 10;
                empty.style.paddingTop = 10;
                list.Add(empty);
                return;
            }

            foreach (var file in _files)
            {
                var entry = file;
                var row = new Button(() => Select(entry, sideA))
                {
                    text = entry.IsUsable ? entry.DisplayName : $"{entry.fileName} — unreadable ({entry.loadError})"
                };
                row.AddToClassList("aero-cmp-file");
                row.SetEnabled(entry.IsUsable);
                row.focusable = false;
                row.tooltip = entry.path;

                bool selected = sideA ? _selectedA == entry : _selectedB == entry;
                if (selected) row.AddToClassList(sideA ? "aero-cmp-file-a" : "aero-cmp-file-b");
                list.Add(row);
            }
        }

        void Select(AeroReportFile file, bool sideA)
        {
            if (sideA)
            {
                _selectedA = file;
                _testIndexA = 0;
                _testIndexB = -1;
            }
            else
            {
                _selectedB = file;
                _testIndexB = -1;
            }
            FillList(_listA, true);
            FillList(_listB, false);
            RefreshTestDropdowns();
            Rebuild();
        }

        void RefreshTestDropdowns()
        {
            FillTestDropdown(_testA, _selectedA, ref _testIndexA);
            int shown = _testIndexB;
            FillTestDropdown(_testB, _selectedB, ref shown);
        }

        static void FillTestDropdown(DropdownField field, AeroReportFile file, ref int index)
        {
            var names = new List<string>();
            if (file != null && file.IsUsable)
                foreach (var test in file.session.tests)
                    names.Add($"{test.testName} ({test.kind})");

            field.choices = names;
            field.SetEnabled(names.Count > 1);
            if (names.Count == 0)
            {
                field.SetValueWithoutNotify("—");
                return;
            }
            index = Mathf.Clamp(index, 0, names.Count - 1);
            field.SetValueWithoutNotify(names[index]);
        }

        // ------------------------------------------------------------------ result

        void Rebuild()
        {
            _resultHost.Clear();
            CurrentReport = null;
            _exportButton?.SetEnabled(false);
            SetExportNote("", Muted);

            if (_selectedA == null || _selectedB == null)
            {
                SetStatus("Select a result on each side.");
                return;
            }
            if (!_selectedA.IsUsable || !_selectedB.IsUsable)
            {
                SetStatus("One of the selected files could not be read.");
                return;
            }

            var report = AeroComparison.Compare(_selectedA.session, _selectedB.session, _testIndexA, _testIndexB);
            CurrentReport = report;
            _exportButton?.SetEnabled(report.Valid);
            if (!report.Valid)
            {
                SetStatus(report.error.ToUpperInvariant());
                _resultHost.Add(Banner("CANNOT COMPARE", report.error, Bad, 0));
                return;
            }

            // The matcher may have chosen B's test for us; show what it picked.
            int matchedB = report.sessionB.tests.IndexOf(report.testB);
            if (matchedB >= 0)
            {
                int shown = matchedB;
                FillTestDropdown(_testB, _selectedB, ref shown);
            }

            SetStatus(null, report);

            _resultHost.Add(Banner(
                report.winner == 0 ? report.verdict.ToUpperInvariant() : "WINNER",
                report.verdictDetail,
                !report.comparable ? Bad : report.winner == 0 ? Warn : Good,
                report.winner,
                // The banner draws the side chip itself, so pass the name alone —
                // otherwise it reads "WINNER: B  B - McLaren".
                report.winner == 0 ? null : report.winnerName));

            _resultHost.Add(SectionTitle("LIKE-FOR-LIKE AUDIT"));
            _resultHost.Add(BuildChecks(report));

            _resultHost.Add(SectionTitle(report.testA.kind == AeroTestKind.ConstantSpeedDrag
                ? "MEASUREMENTS"
                : "SWEEP AVERAGES"));
            _resultHost.Add(BuildMetricTable(report));

            if (report.sweep.Count > 0)
            {
                _resultHost.Add(SectionTitle($"POINT BY POINT — {report.testA.parameterName.ToUpperInvariant()}"));
                _resultHost.Add(BuildSweepTable(report));
            }
        }

        /// <summary>
        /// The status strip under the pickers: plain text while idle, and the two
        /// badged run names once both sides are chosen.
        /// </summary>
        void SetStatus(string text, AeroComparisonReport report = null)
        {
            var parent = _statusLabel.parent;
            int index = parent.IndexOf(_statusLabel);

            // Rebuilt in place: it alternates between a plain label and a badged row.
            var existing = parent.Q<VisualElement>("compare-status-row");
            existing?.RemoveFromHierarchy();

            if (report == null)
            {
                _statusLabel.style.display = DisplayStyle.Flex;
                _statusLabel.text = text ?? "";
                return;
            }

            _statusLabel.style.display = DisplayStyle.None;
            var row = new VisualElement { name = "compare-status-row" };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 10;
            row.style.flexWrap = Wrap.Wrap;

            row.Add(Badge("A", SideA));
            row.Add(RunLabel(report.labelA, SideA));
            var arrow = new Label("vs") { style = { fontSize = 10, color = Muted, marginLeft = 12, marginRight = 12 } };
            arrow.style.letterSpacing = 2;
            row.Add(arrow);
            row.Add(Badge("B", SideB));
            row.Add(RunLabel(report.labelB, SideB));

            parent.Insert(index + 1, row);
        }

        static Label RunLabel(string text, Color side)
        {
            return new Label(text)
            {
                style =
                {
                    fontSize = 11,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = side,
                    letterSpacing = 1
                }
            };
        }

        VisualElement BuildChecks(AeroComparisonReport report)
        {
            var table = TableBox();
            table.Add(TableHeader(
                new[] { "CHECK", "A", "B", "" },
                new[] { 22f, 25f, 25f, 28f },
                new[] { ColumnMark.None, ColumnMark.A, ColumnMark.B, ColumnMark.None }));

            foreach (var check in report.checks)
            {
                Color color = check.level switch
                {
                    ComparabilityLevel.Blocking => Bad,
                    ComparabilityLevel.Warning => Warn,
                    ComparabilityLevel.Note => Muted,
                    _ => Good
                };
                string mark = check.level switch
                {
                    ComparabilityLevel.Blocking => "BLOCKS COMPARISON",
                    ComparabilityLevel.Warning => "CAVEAT",
                    ComparabilityLevel.Note => "NOTED",
                    _ => "MATCH"
                };

                var row = TableRow();
                row.Add(Cell(check.label, 22f, Muted));
                row.Add(Cell(check.a, 25f, Text, ColumnMark.A));
                row.Add(Cell(check.b, 25f, Text, ColumnMark.B));

                var verdictCell = new VisualElement { style = { flexBasis = Length.Percent(28f), flexGrow = 0 } };
                verdictCell.Add(CellLabel(mark, color, bold: true));
                if (!string.IsNullOrEmpty(check.note))
                {
                    var note = CellLabel(check.note, Muted);
                    note.style.fontSize = 10;
                    note.style.whiteSpace = WhiteSpace.Normal;
                    note.style.overflow = Overflow.Visible;
                    verdictCell.Add(note);
                }
                row.Add(verdictCell);
                table.Add(row);
            }
            return table;
        }

        VisualElement BuildMetricTable(AeroComparisonReport report)
        {
            var table = TableBox();
            table.Add(TableHeader(
                new[] { "METRIC", "A", "B", "Δ", "Δ %", "BETTER" },
                new[] { 28f, 13f, 13f, 13f, 13f, 20f },
                new[] { ColumnMark.None, ColumnMark.A, ColumnMark.B, ColumnMark.None, ColumnMark.None, ColumnMark.None }));

            foreach (var row in report.rows)
            {
                var line = TableRow();
                if (row.primary)
                    line.style.backgroundColor = new Color(Accent.r, Accent.g, Accent.b, 0.07f);

                string unit = string.IsNullOrEmpty(row.unit) ? "" : " " + row.unit;
                line.Add(Cell(row.label + (row.primary ? "  ◄ primary" : ""), 28f, row.primary ? Accent : Muted));
                line.Add(Cell(row.a.ToString(row.format) + unit, 13f, Text, ColumnMark.A));
                line.Add(Cell(row.b.ToString(row.format) + unit, 13f, Text, ColumnMark.B));
                line.Add(Cell((row.delta >= 0f ? "+" : "") + row.delta.ToString(row.format), 13f,
                              row.better == 0 ? Muted : Text));
                line.Add(Cell(row.hasDeltaPct ? (row.deltaPct >= 0f ? "+" : "") + row.deltaPct.ToString("0.0") + "%" : "—",
                              13f, row.better == 0 ? Muted : Text));

                // The winning side is named by its identity colour, so "better" reads as
                // "side B won this row" rather than as a second meaning for green.
                if (row.polarity == MetricPolarity.Informational)
                {
                    line.Add(Cell("—", 20f, Muted));
                }
                else if (row.withinNoise)
                {
                    line.Add(Cell("within noise", 20f, Muted));
                }
                else
                {
                    var cell = new VisualElement { style = { flexBasis = Length.Percent(20f), flexGrow = 0 } };
                    cell.style.flexDirection = FlexDirection.Row;
                    cell.style.alignItems = Align.Center;
                    cell.Add(Badge(row.better > 0 ? "B" : "A", row.better > 0 ? SideB : SideA, 16));
                    cell.Add(CellLabel("better", Good));
                    line.Add(cell);
                }

                table.Add(line);
            }

            var footnote = Caption($"Deltas are B − A. ±{report.noiseBandPct:0.0}% is the uncertainty on these two " +
                                   "means (standard error over the averaged flow-throughs); anything inside it is not a result.");
            footnote.style.whiteSpace = WhiteSpace.Normal;
            footnote.style.marginTop = 8;
            table.Add(footnote);
            return table;
        }

        VisualElement BuildSweepTable(AeroComparisonReport report)
        {
            var table = TableBox();
            table.Add(TableHeader(
                new[] { "POINT", "Cd A", "Cd B", "Δ Cd", "Cl A", "Cl B", "Cy A", "Cy B" },
                new[] { 16f, 12f, 12f, 12f, 12f, 12f, 12f, 12f },
                new[] { ColumnMark.None, ColumnMark.A, ColumnMark.B, ColumnMark.None,
                        ColumnMark.A, ColumnMark.B, ColumnMark.A, ColumnMark.B }));

            foreach (var row in report.sweep)
            {
                var line = TableRow();
                bool suspect = !row.convergedA || !row.convergedB;
                line.Add(Cell(row.parameter.ToString("0.###") + (suspect ? " *" : ""), 16f, suspect ? Warn : Muted));
                line.Add(Cell(row.cdA.ToString("0.000"), 12f, Text, ColumnMark.A));
                line.Add(Cell(row.cdB.ToString("0.000"), 12f, Text, ColumnMark.B));
                float d = row.cdB - row.cdA;
                line.Add(Cell((d >= 0f ? "+" : "") + d.ToString("0.000"), 12f, d < 0f ? Good : Bad));
                line.Add(Cell(row.clA.ToString("0.000"), 12f, Text, ColumnMark.A));
                line.Add(Cell(row.clB.ToString("0.000"), 12f, Text, ColumnMark.B));
                line.Add(Cell(row.cyA.ToString("0.000"), 12f, Text, ColumnMark.A));
                line.Add(Cell(row.cyB.ToString("0.000"), 12f, Text, ColumnMark.B));
                table.Add(line);
            }

            var note = Caption("* point did not converge before the step cap.");
            note.style.marginTop = 8;
            table.Add(note);
            return table;
        }

        // ------------------------------------------------------------------ widgets

        enum ColumnMark { None, A, B }

        static VisualElement Row() => new VisualElement { style = { flexDirection = FlexDirection.Row } };

        static void Border(VisualElement element, Color color, float width, float radius)
        {
            element.style.borderTopWidth = element.style.borderBottomWidth =
                element.style.borderLeftWidth = element.style.borderRightWidth = width;
            element.style.borderTopColor = element.style.borderBottomColor =
                element.style.borderLeftColor = element.style.borderRightColor = color;
            element.style.borderTopLeftRadius = element.style.borderTopRightRadius =
                element.style.borderBottomLeftRadius = element.style.borderBottomRightRadius = radius;
        }

        /// <summary>The A / B chip. One element, one meaning, used everywhere a side is named.</summary>
        static Label Badge(string letter, Color side, float size = 19f)
        {
            var badge = new Label(letter);
            badge.AddToClassList("aero-cmp-badge");
            badge.AddToClassList(side == SideA ? "aero-cmp-badge-a" : "aero-cmp-badge-b");
            // Size only: colour and border belong to the stylesheet, and an inline
            // style would outrank it (inline beats USS in UI Toolkit).
            badge.style.width = size;
            badge.style.height = size;
            return badge;
        }

        static Label Title(string text) => new Label(text)
        {
            style =
            {
                fontSize = 17, unityFontStyleAndWeight = FontStyle.Bold,
                letterSpacing = 4, color = Text
            }
        };

        static Label SectionTitle(string text)
        {
            var label = new Label(text);
            label.AddToClassList("aero-cmp-section");
            label.style.fontSize = 11;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.letterSpacing = 3;
            label.style.color = Muted;
            label.style.marginTop = 16;
            label.style.marginBottom = 6;
            return label;
        }

        static Label Caption(string text) => new Label(text)
        {
            style = { fontSize = 10, letterSpacing = 1, color = Muted, marginTop = 3 }
        };

        static VisualElement Hairline()
        {
            var line = new VisualElement { style = { height = 1, marginTop = 10 } };
            line.style.backgroundColor = Line;
            return line;
        }

        static Button Btn(string text, Action action, string tooltip = null)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("aero-cmp-btn");
            button.focusable = false;
            button.style.marginLeft = 6;
            if (!string.IsNullOrEmpty(tooltip)) button.tooltip = tooltip;
            return button;
        }

        static VisualElement Banner(string title, string detail, Color color, int winner, string winnerName = null)
        {
            var box = new VisualElement();
            box.AddToClassList("aero-cmp-verdict");
            box.style.backgroundColor = new Color(color.r, color.g, color.b, 0.12f);
            Border(box, color, 1, 3);
            box.style.paddingLeft = box.style.paddingRight = 12;
            box.style.paddingTop = box.style.paddingBottom = 10;
            box.style.marginTop = 10;

            var titleRow = Row();
            titleRow.style.alignItems = Align.Center;
            if (winner != 0)
                titleRow.Add(Badge(winner > 0 ? "B" : "A", winner > 0 ? SideB : SideA, 21));
            titleRow.Add(new Label(winnerName == null ? title : $"{title}: {winnerName.ToUpperInvariant()}")
            {
                style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, letterSpacing = 2, color = color }
            });
            box.Add(titleRow);

            box.Add(new Label(detail)
            {
                style = { fontSize = 11, color = Text, whiteSpace = WhiteSpace.Normal, marginTop = 5 }
            });
            return box;
        }

        static VisualElement TableBox()
        {
            var box = new VisualElement();
            box.AddToClassList("aero-cmp-table");
            box.style.backgroundColor = Panel;
            Border(box, Line, 1, 3);
            box.style.paddingLeft = box.style.paddingRight = 10;
            box.style.paddingTop = box.style.paddingBottom = 8;
            return box;
        }

        static VisualElement TableHeader(string[] labels, float[] widths, ColumnMark[] marks)
        {
            var row = Row();
            row.AddToClassList("aero-cmp-head");
            row.style.paddingBottom = 6;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = Line;

            for (int i = 0; i < labels.Length; i++)
            {
                ColumnMark mark = marks != null && i < marks.Length ? marks[i] : ColumnMark.None;
                Color side = mark == ColumnMark.A ? SideA : SideB;

                // A bare "A"/"B" header becomes the identity chip itself; a labelled
                // one (e.g. "Cd A") is tinted instead, so the table never repeats the
                // same badge eight times.
                if (mark != ColumnMark.None && labels[i].Length == 1)
                {
                    var cell = new VisualElement { style = { flexBasis = Length.Percent(widths[i]), flexGrow = 0 } };
                    cell.style.flexDirection = FlexDirection.Row;
                    cell.style.alignItems = Align.Center;
                    MarkColumn(cell, mark);
                    cell.Add(Badge(labels[i], side, 19));
                    row.Add(cell);
                    continue;
                }

                var header = CellLabel(labels[i], mark == ColumnMark.None ? Muted : side, bold: true);
                header.style.fontSize = 10;
                header.style.letterSpacing = 2;
                header.style.flexBasis = Length.Percent(widths[i]);
                MarkColumn(header, mark);
                row.Add(header);
            }
            return row;
        }

        static void MarkColumn(VisualElement element, ColumnMark mark)
        {
            if (mark == ColumnMark.None) return;
            element.AddToClassList(mark == ColumnMark.A ? "aero-cmp-col-a" : "aero-cmp-col-b");
        }

        static VisualElement TableRow()
        {
            var row = Row();
            row.AddToClassList("aero-cmp-row");
            row.style.paddingTop = row.style.paddingBottom = 4;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new Color(Line.r, Line.g, Line.b, 0.5f);
            return row;
        }

        static Label Cell(string text, float widthPercent, Color color, ColumnMark mark = ColumnMark.None)
        {
            var label = CellLabel(text, color);
            label.style.flexBasis = Length.Percent(widthPercent);
            MarkColumn(label, mark);
            return label;
        }

        static Label CellLabel(string text, Color color, bool bold = false)
        {
            var label = new Label(text)
            {
                style =
                {
                    fontSize = 11,
                    color = color,
                    flexGrow = 0,
                    flexShrink = 1,
                    unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal
                }
            };
            // Keep a long vehicle name or grid string inside its column instead of
            // letting it run over the next one.
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.tooltip = text;
            return label;
        }
    }
}
