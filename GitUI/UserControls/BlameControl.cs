﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using GitCommands;
using GitExtUtils;
using GitUI.CommitInfo;
using GitUI.Editor;
using GitUI.HelperDialogs;
using GitUIPluginInterfaces;
using JetBrains.Annotations;

namespace GitUI.Blame
{
    public sealed partial class BlameControl : GitModuleControl
    {
        public event EventHandler<CommandEventArgs> CommandClick;

        /// <summary>
        /// Raised when the Escape key is pressed (and only when no selection exists, as the default behaviour of escape is to clear the selection).
        /// </summary>
        public event Action EscapePressed;

        private readonly AsyncLoader _blameLoader = new AsyncLoader();

        [CanBeNull] private GitBlameLine _lastBlameLine;
        [CanBeNull] private GitBlameLine _clickedBlameLine;
        private GitBlameCommit _highlightedCommit;
        private GitBlame _blame;
        private RevisionGridControl _revGrid;
        [CanBeNull] private ObjectId _blameId;
        private string _fileName;
        private Encoding _encoding;
        private int _lastTooltipX = -100;
        private int _lastTooltipY = -100;
        private GitBlameCommit _tooltipCommit;
        private bool _changingScrollPosition;

        public BlameControl()
        {
            InitializeComponent();
            InitializeComplete();

            BlameAuthor.IsReadOnly = true;
            BlameAuthor.EnableScrollBars(false);
            UpdateShowLineNumbers();
            BlameAuthor.HScrollPositionChanged += BlameAuthor_HScrollPositionChanged;
            BlameAuthor.VScrollPositionChanged += BlameAuthor_VScrollPositionChanged;
            BlameAuthor.MouseMove += BlameAuthor_MouseMove;
            BlameAuthor.MouseLeave += BlameAuthor_MouseLeave;
            BlameAuthor.SelectedLineChanged += SelectedLineChanged;
            BlameAuthor.RequestDiffView += ActiveTextAreaControlDoubleClick;
            BlameAuthor.EscapePressed += () => EscapePressed?.Invoke();

            BlameFile.IsReadOnly = true;
            BlameFile.VScrollPositionChanged += BlameFile_VScrollPositionChanged;
            BlameFile.SelectedLineChanged += SelectedLineChanged;
            BlameFile.RequestDiffView += ActiveTextAreaControlDoubleClick;
            BlameFile.MouseMove += BlameFile_MouseMove;
            BlameFile.EscapePressed += () => EscapePressed?.Invoke();

            CommitInfo.CommandClicked += commitInfo_CommandClicked;
        }

        public void UpdateShowLineNumbers()
        {
            BlameAuthor.ShowLineNumbers = AppSettings.BlameShowLineNumbers;
        }

        public void LoadBlame(GitRevision revision, [CanBeNull] IReadOnlyList<ObjectId> children, string fileName, RevisionGridControl revGrid, Control controlToMask, Encoding encoding, int? initialLine = null, bool force = false)
        {
            var objectId = revision.ObjectId;

            // refresh only when something changed
            if (!force && objectId == _blameId && fileName == _fileName && revGrid == _revGrid && encoding == _encoding)
            {
                return;
            }

            controlToMask?.Mask();

            var scrollPos = BlameFile.VScrollPosition;

            var line = _clickedBlameLine != null && _clickedBlameLine.Commit.ObjectId == objectId
                ? _clickedBlameLine.OriginLineNumber
                : initialLine ?? 0;

            _revGrid = revGrid;
            _fileName = fileName;
            _encoding = encoding;

            _blameLoader.LoadAsync(() => _blame = Module.Blame(fileName, objectId.ToString(), encoding),
                () => ProcessBlame(fileName, revision, children, controlToMask, line, scrollPos));
        }

        private void commitInfo_CommandClicked(object sender, CommandEventArgs e)
        {
            CommandClick?.Invoke(sender, e);
        }

        private void BlameAuthor_MouseLeave(object sender, EventArgs e)
        {
            blameTooltip.Hide(this);
        }

        private void BlameAuthor_MouseMove(object sender, MouseEventArgs e)
        {
            if (!BlameFile.Focused)
            {
                BlameFile.Focus();
            }

            if (_blame == null)
            {
                return;
            }

            var lineIndex = BlameAuthor.GetLineFromVisualPosY(e.Y);

            var blameCommit = lineIndex < _blame.Lines.Count
                ? _blame.Lines[lineIndex].Commit
                : null;

            HighlightLinesForCommit(blameCommit);

            if (blameCommit == null)
            {
                return;
            }

            int newTooltipX = splitContainer2.SplitterDistance + 60;
            int newTooltipY = e.Y + splitContainer1.SplitterDistance + 20;

            if (_tooltipCommit != blameCommit || Math.Abs(_lastTooltipX - newTooltipX) > 5 || Math.Abs(_lastTooltipY - newTooltipY) > 5)
            {
                _tooltipCommit = blameCommit;
                _lastTooltipX = newTooltipX;
                _lastTooltipY = newTooltipY;
                blameTooltip.Show(blameCommit.ToString(), this, newTooltipX, newTooltipY);
            }
        }

        private void BlameFile_MouseMove(object sender, MouseEventArgs e)
        {
            if (_blame == null)
            {
                return;
            }

            var lineIndex = BlameFile.GetLineFromVisualPosY(e.Y);

            var blameCommit = lineIndex < _blame.Lines.Count
                ? _blame.Lines[lineIndex].Commit
                : null;

            HighlightLinesForCommit(blameCommit);
        }

        private void HighlightLinesForCommit([CanBeNull] GitBlameCommit commit)
        {
            if (commit == _highlightedCommit)
            {
                return;
            }

            _highlightedCommit = commit;

            BlameAuthor.ClearHighlighting();
            BlameFile.ClearHighlighting();

            if (commit == null)
            {
                return;
            }

            int startLine = -1;
            int prevLine = -1;
            for (int i = 0; i < _blame.Lines.Count; i++)
            {
                if (ReferenceEquals(_blame.Lines[i].Commit, commit))
                {
                    if (prevLine != i - 1 && startLine != -1)
                    {
                        BlameAuthor.HighlightLines(startLine, prevLine, SystemColors.ControlLight);
                        BlameFile.HighlightLines(startLine, prevLine, SystemColors.ControlLight);
                        startLine = -1;
                    }

                    prevLine = i;
                    if (startLine == -1)
                    {
                        startLine = i;
                    }
                }
            }

            if (startLine != -1)
            {
                BlameAuthor.HighlightLines(startLine, prevLine, SystemColors.ControlLight);
                BlameFile.HighlightLines(startLine, prevLine, SystemColors.ControlLight);
            }

            BlameAuthor.Refresh();
            BlameFile.Refresh();
        }

        private void SelectedLineChanged(object sender, SelectedLineEventArgs e)
        {
            int selectedLine = e.SelectedLine;

            if (_blame == null || selectedLine >= _blame.Lines.Count)
            {
                return;
            }

            // TODO: Request GitRevision from RevisionGrid that contain all commits
            var newBlameLine = _blame.Lines[selectedLine];

            if (ReferenceEquals(_lastBlameLine?.Commit, newBlameLine.Commit))
            {
                return;
            }

            _lastBlameLine = newBlameLine;
            CommitInfo.Revision = Module.GetRevision(_lastBlameLine.Commit.ObjectId);
        }

        private void BlameAuthor_HScrollPositionChanged(object sender, EventArgs e)
        {
            BlameAuthor.HScrollPosition = 0;
        }

        private void BlameAuthor_VScrollPositionChanged(object sender, EventArgs e)
        {
            if (!_changingScrollPosition)
            {
                _changingScrollPosition = true;
                BlameFile.VScrollPosition = BlameAuthor.VScrollPosition;
                _changingScrollPosition = false;
            }

            Rectangle rect = BlameAuthor.ClientRectangle;
            rect = BlameAuthor.RectangleToScreen(rect);
            if (rect.Contains(MousePosition))
            {
                Point p = BlameAuthor.PointToClient(MousePosition);
                var me = new MouseEventArgs(0, 0, p.X, p.Y, 0);
                BlameAuthor_MouseMove(null, me);
            }
        }

        private void BlameFile_VScrollPositionChanged(object sender, EventArgs e)
        {
            if (_changingScrollPosition)
            {
                return;
            }

            _changingScrollPosition = true;
            BlameAuthor.VScrollPosition = BlameFile.VScrollPosition;
            _changingScrollPosition = false;
        }

        private void ProcessBlame(string filename, GitRevision revision, IReadOnlyList<ObjectId> children, Control controlToMask, int lineNumber, int scrollpos)
        {
            var body = new StringBuilder(capacity: 4096);

            GitBlameCommit lastCommit = null;

            var dateTimeFormat = AppSettings.BlameShowAuthorTime
                ? CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern + " " + CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern
                : CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;

            // NOTE EOL white-space supports highlight on mouse-over.
            // Highlighting is done via text background colour.
            // If it could be done with a solid rectangle around the text,
            // the extra spaces added here could be omitted.

            var filePathLengthEstimate = _blame.Lines.Where(l => filename != l.Commit.FileName)
                .Select(l => l.Commit.FileName.Length)
                .DefaultIfEmpty(0)
                .Max();
            var lineLengthEstimate = 25 + _blame.Lines.Max(l => l.Commit.Author?.Length ?? 0) + filePathLengthEstimate;
            var lineLength = Math.Max(80, lineLengthEstimate);
            var lineBuilder = new StringBuilder(lineLength + 2);
            var gutter = new StringBuilder(capacity: lineBuilder.Capacity * _blame.Lines.Count);
            var emptyLine = new string(' ', lineLength);
            foreach (var line in _blame.Lines)
            {
                if (line.Commit == lastCommit)
                {
                    gutter.AppendLine(emptyLine);
                }
                else
                {
                    BuildAuthorLine(line, lineBuilder, dateTimeFormat, filename);

                    gutter.Append(lineBuilder);
                    gutter.Append(' ', lineLength - lineBuilder.Length).AppendLine();
                    lineBuilder.Clear();
                }

                body.AppendLine(line.Text);

                lastCommit = line.Commit;
            }

            ThreadHelper.JoinableTaskFactory.RunAsync(
                () => BlameAuthor.ViewTextAsync("committer.txt", gutter.ToString()));
            ThreadHelper.JoinableTaskFactory.RunAsync(
                () => BlameFile.ViewTextAsync(_fileName, body.ToString()));

            if (lineNumber > 0)
            {
                BlameFile.GoToLine(lineNumber - 1);
            }
            else
            {
                BlameFile.VScrollPosition = scrollpos;
            }

            _clickedBlameLine = null;

            _blameId = revision.ObjectId;
            CommitInfo.SetRevisionWithChildren(revision, children);

            controlToMask?.UnMask();
        }

        private void BuildAuthorLine(GitBlameLine line, StringBuilder lineBuilder, string dateTimeFormat, string filename)
        {
            if (AppSettings.BlameShowAuthor && AppSettings.BlameDisplayAuthorFirst)
            {
                lineBuilder.Append(line.Commit.Author);
                if (AppSettings.BlameShowAuthorDate)
                {
                    lineBuilder.Append(" - ");
                }
            }

            if (AppSettings.BlameShowAuthorDate)
            {
                lineBuilder.Append(line.Commit.AuthorTime.ToString(dateTimeFormat));
            }

            if (AppSettings.BlameShowAuthor && !AppSettings.BlameDisplayAuthorFirst)
            {
                if (AppSettings.BlameShowAuthorDate)
                {
                    lineBuilder.Append(" - ");
                }

                lineBuilder.Append(line.Commit.Author);
            }

            if (AppSettings.BlameShowOriginalFilePath && filename != line.Commit.FileName)
            {
                lineBuilder.Append(" - ");
                lineBuilder.Append(line.Commit.FileName);
            }
        }

        private void ActiveTextAreaControlDoubleClick(object sender, EventArgs e)
        {
            if (_lastBlameLine == null)
            {
                return;
            }

            if (_revGrid != null)
            {
                _clickedBlameLine = _lastBlameLine;
                _revGrid.SetSelectedRevision(_lastBlameLine.Commit.ObjectId);
            }
            else
            {
                using (var frm = new FormCommitDiff(UICommands, _lastBlameLine.Commit.ObjectId))
                {
                    frm.ShowDialog(this);
                }
            }
        }

        private int GetBlameLine()
        {
            if (_blame == null)
            {
                return -1;
            }

            Point position = BlameAuthor.PointToClient(MousePosition);

            int line = BlameAuthor.GetLineFromVisualPosY(position.Y);

            if (line >= _blame.Lines.Count)
            {
                return -1;
            }

            return line;
        }

        private void contextMenu_Opened(object sender, EventArgs e)
        {
            contextMenu.Tag = GetBlameLine();
        }

        [CanBeNull]
        private GitBlameCommit GetBlameCommit()
        {
            int line = (int?)contextMenu.Tag ?? -1;

            if (line < 0)
            {
                return null;
            }

            return _blame.Lines[line].Commit;
        }

        private void copyLogMessageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CopyToClipboard(c => c.Summary);
        }

        private void CopyToClipboard(Func<GitBlameCommit, string> formatter)
        {
            var commit = GetBlameCommit();

            if (commit == null)
            {
                return;
            }

            ClipboardUtil.TrySetText(formatter(commit));
        }

        private void copyAllCommitInfoToClipboardToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CopyToClipboard(c => c.ToString());
        }

        private void copyCommitHashToClipboardToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CopyToClipboard(c => c.ObjectId.ToString());
        }

        private void blamePreviousRevisionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            int line = (int?)contextMenu.Tag ?? -1;
            if (line < 0)
            {
                return;
            }

            var objectId = _blame.Lines[line].Commit.ObjectId;
            int originalLine = _blame.Lines[line].OriginLineNumber;
            GitBlame blame = Module.Blame(_fileName, objectId + "^", _encoding, originalLine + ",+1");
            if (blame.Lines.Count > 0)
            {
                var revision = blame.Lines[0].Commit.ObjectId;
                if (_revGrid != null)
                {
                    _clickedBlameLine = blame.Lines[0];
                    _revGrid.SetSelectedRevision(revision);
                }
                else
                {
                    using (var frm = new FormCommitDiff(UICommands, revision))
                    {
                        frm.ShowDialog(this);
                    }
                }
            }
        }

        private void showChangesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var commit = GetBlameCommit();

            if (commit == null)
            {
                return;
            }

            using (var frm = new FormCommitDiff(UICommands, commit.ObjectId))
            {
                frm.ShowDialog(this);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                components?.Dispose();
                _blameLoader.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
