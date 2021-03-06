﻿using System;
using System.Collections.Generic;
using System.Linq;
using EditorUtils.UnitTest;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Moq;
using NUnit.Framework;
using Vim.Extensions;
using Vim.UnitTest.Mock;

namespace Vim.UnitTest
{
    // TODO: Need to remove several of the mock's here.  No reason to mock IVimLocalSettings and 
    // a couple others.
    [TestFixture]
    public sealed class CommonOperationsTest : VimTestBase
    {
        private ITextView _textView;
        private ITextBuffer _textBuffer;
        private IFoldManager _foldManager;
        private MockRepository _factory;
        private Mock<IVimHost> _vimHost;
        private Mock<IJumpList> _jumpList;
        private Mock<IVimLocalSettings> _localSettings;
        private Mock<IVimGlobalSettings> _globalSettings;
        private Mock<IOutliningManager> _outlining;
        private Mock<IStatusUtil> _statusUtil;
        private Mock<ISmartIndentationService> _smartIndentationService;
        private Mock<IVimTextBuffer> _vimTextBuffer;
        private IUndoRedoOperations _undoRedoOperations;
        private ISearchService _searchService;
        private IVimData _vimData;
        private ICommonOperations _operations;
        private CommonOperations _operationsRaw;

        private Register UnnamedRegister
        {
            get { return Vim.RegisterMap.GetRegister(RegisterName.Unnamed); }
        }

        public void Create(params string[] lines)
        {
            _textView = CreateTextView(lines);
            _textView.Caret.MoveTo(new SnapshotPoint(_textView.TextSnapshot, 0));
            _textBuffer = _textView.TextBuffer;
            _foldManager = FoldManagerFactory.GetFoldManager(_textView);
            _factory = new MockRepository(MockBehavior.Strict);

            // Create the Vim instance with our Mock'd services
            _vimData = new VimData();
            var registerMap = VimUtil.CreateRegisterMap(MockObjectFactory.CreateClipboardDevice(_factory).Object);
            _vimHost = _factory.Create<IVimHost>();
            _globalSettings = _factory.Create<IVimGlobalSettings>();
            _globalSettings.SetupGet(x => x.Magic).Returns(true);
            _globalSettings.SetupGet(x => x.SmartCase).Returns(false);
            _globalSettings.SetupGet(x => x.IgnoreCase).Returns(true);
            _globalSettings.SetupGet(x => x.IsVirtualEditOneMore).Returns(false);
            _globalSettings.SetupGet(x => x.UseEditorIndent).Returns(false);
            _globalSettings.SetupGet(x => x.UseEditorSettings).Returns(false);
            _globalSettings.SetupGet(x => x.VirtualEdit).Returns(String.Empty);
            _globalSettings.SetupGet(x => x.WrapScan).Returns(true);
            _globalSettings.SetupGet(x => x.ShiftWidth).Returns(2);
            _searchService = new SearchService(TextSearchService, _globalSettings.Object);
            var vim = MockObjectFactory.CreateVim(
                registerMap: registerMap,
                host: _vimHost.Object,
                settings: _globalSettings.Object,
                searchService: _searchService,
                factory: _factory);

            // Create the IVimTextBuffer instance with our Mock'd services
            _localSettings = MockObjectFactory.CreateLocalSettings(_globalSettings.Object, _factory);
            _localSettings.SetupGet(x => x.AutoIndent).Returns(false);
            _localSettings.SetupGet(x => x.GlobalSettings).Returns(_globalSettings.Object);
            _localSettings.SetupGet(x => x.ExpandTab).Returns(true);
            _localSettings.SetupGet(x => x.TabStop).Returns(4);
            _vimTextBuffer = MockObjectFactory.CreateVimTextBuffer(
                _textBuffer,
                localSettings: _localSettings.Object,
                vim: vim.Object,
                factory: _factory);

            // Create the VimBufferData instance with our Mock'd services
            _jumpList = _factory.Create<IJumpList>();
            _statusUtil = _factory.Create<IStatusUtil>();
            _undoRedoOperations = VimUtil.CreateUndoRedoOperations(_statusUtil.Object);
            var vimBufferData = CreateVimBufferData(
                _vimTextBuffer.Object,
                _textView,
                statusUtil: _statusUtil.Object,
                jumpList: _jumpList.Object,
                undoRedoOperations: _undoRedoOperations);

            _smartIndentationService = _factory.Create<ISmartIndentationService>();
            _outlining = _factory.Create<IOutliningManager>();
            _outlining
                .Setup(x => x.ExpandAll(It.IsAny<SnapshotSpan>(), It.IsAny<Predicate<ICollapsed>>()))
                .Returns<IEnumerable<ICollapsible>>(null);

            _operationsRaw = new CommonOperations(
                vimBufferData,
                EditorOperationsFactoryService.GetEditorOperations(_textView),
                FSharpOption.Create(_outlining.Object),
                _smartIndentationService.Object);
            _operations = _operationsRaw;
        }

        [TearDown]
        public void TearDown()
        {
            _operations = null;
            _operationsRaw = null;
        }

        private static string CreateLinesWithLineBreak(params string[] lines)
        {
            return lines.Aggregate((x, y) => x + Environment.NewLine + y) + Environment.NewLine;
        }

        /// <summary>
        /// Standard case of deleting several lines in the buffer
        /// </summary>
        [Test]
        public void DeleteLines_Multiple()
        {
            Create("cat", "dog", "bear");
            _operations.DeleteLines(_textBuffer.GetLine(0), 2, UnnamedRegister);
            Assert.AreEqual(CreateLinesWithLineBreak("cat", "dog"), UnnamedRegister.StringValue);
            Assert.AreEqual("bear", _textView.GetLine(0).GetText());
            Assert.AreEqual(OperationKind.LineWise, UnnamedRegister.OperationKind);
        }

        /// <summary>
        /// Verify the deleting of lines where the count causes the deletion to cross 
        /// over a fold
        /// </summary>
        [Test]
        public void DeleteLines_OverFold()
        {
            Create("cat", "dog", "bear", "fish", "tree");
            _foldManager.CreateFold(_textView.GetLineRange(1, 2));
            _operations.DeleteLines(_textBuffer.GetLine(0), 3, UnnamedRegister);
            Assert.AreEqual(CreateLinesWithLineBreak("cat", "dog", "bear", "fish"), UnnamedRegister.StringValue);
            Assert.AreEqual("tree", _textView.GetLine(0).GetText());
            Assert.AreEqual(OperationKind.LineWise, UnnamedRegister.OperationKind);
        }

        /// <summary>
        /// Verify the deleting of lines where the count causes the deletion to cross 
        /// over a fold which begins the deletion span
        /// </summary>
        [Test]
        public void DeleteLines_StartOfFold()
        {
            Create("cat", "dog", "bear", "fish", "tree");
            _foldManager.CreateFold(_textView.GetLineRange(0, 1));
            _operations.DeleteLines(_textBuffer.GetLine(0), 2, UnnamedRegister);
            Assert.AreEqual(CreateLinesWithLineBreak("cat", "dog", "bear"), UnnamedRegister.StringValue);
            Assert.AreEqual("fish", _textView.GetLine(0).GetText());
            Assert.AreEqual(OperationKind.LineWise, UnnamedRegister.OperationKind);
        }

        [Test]
        public void DeleteLines_Simple()
        {
            Create("foo", "bar", "baz", "jaz");
            _operations.DeleteLines(_textBuffer.GetLine(0), 1, UnnamedRegister);
            Assert.AreEqual("bar", _textView.GetLine(0).GetText());
            Assert.AreEqual("foo" + Environment.NewLine, UnnamedRegister.StringValue);
            Assert.AreEqual(0, _textView.GetCaretPoint().Position);
        }

        [Test]
        public void DeleteLines_WithCount()
        {
            Create("foo", "bar", "baz", "jaz");
            _operations.DeleteLines(_textBuffer.GetLine(0), 2, UnnamedRegister);
            Assert.AreEqual("baz", _textView.GetLine(0).GetText());
            Assert.AreEqual("foo" + Environment.NewLine + "bar" + Environment.NewLine, UnnamedRegister.StringValue);
            Assert.AreEqual(0, _textView.GetCaretPoint().Position);
        }

        /// <summary>
        /// Delete the last line and make sure it actually deletes a line from the buffer
        /// </summary>
        [Test]
        public void DeleteLines_LastLine()
        {
            Create("foo", "bar");
            _operations.DeleteLines(_textBuffer.GetLine(1), 1, UnnamedRegister);
            Assert.AreEqual("bar" + Environment.NewLine, UnnamedRegister.StringValue);
            Assert.AreEqual(1, _textView.TextSnapshot.LineCount);
            Assert.AreEqual("foo", _textView.GetLine(0).GetText());
        }

        /// <summary>
        /// Ensure that a join of 2 lines which don't have any blanks will produce lines which
        /// are separated by a single space
        /// </summary>
        [Test]
        public void Join_RemoveSpaces_NoBlanks()
        {
            Create("foo", "bar");
            _operations.Join(_textView.GetLineRange(0, 1), JoinKind.RemoveEmptySpaces);
            Assert.AreEqual("foo bar", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
            Assert.AreEqual(1, _textView.TextSnapshot.LineCount);
        }

        /// <summary>
        /// Ensure that we properly remove the leading spaces at the start of the next line if
        /// we are removing spaces
        /// </summary>
        [Test]
        public void Join_RemoveSpaces_BlanksStartOfSecondLine()
        {
            Create("foo", "   bar");
            _operations.Join(_textView.GetLineRange(0, 1), JoinKind.RemoveEmptySpaces);
            Assert.AreEqual("foo bar", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
            Assert.AreEqual(1, _textView.TextSnapshot.LineCount);
        }

        /// <summary>
        /// Don't touch the spaces when we join without editing them
        /// </summary>
        [Test]
        public void Join_KeepSpaces_BlanksStartOfSecondLine()
        {
            Create("foo", "   bar");
            _operations.Join(_textView.GetLineRange(0, 1), JoinKind.KeepEmptySpaces);
            Assert.AreEqual("foo   bar", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
            Assert.AreEqual(1, _textView.TextSnapshot.LineCount);
        }

        /// <summary>
        /// Do a join of 3 lines
        /// </summary>
        [Test]
        public void Join_RemoveSpaces_ThreeLines()
        {
            Create("foo", "bar", "baz");
            _operations.Join(_textView.GetLineRange(0, 2), JoinKind.RemoveEmptySpaces);
            Assert.AreEqual("foo bar baz", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
            Assert.AreEqual(1, _textView.TextSnapshot.LineCount);
        }

        /// <summary>
        /// Ensure we can properly join an empty line
        /// </summary>
        [Test]
        public void Join_RemoveSpaces_EmptyLine()
        {
            Create("cat", "", "dog", "tree", "rabbit");
            _operations.Join(_textView.GetLineRange(0, 1), JoinKind.RemoveEmptySpaces);
            Assert.AreEqual("cat ", _textView.GetLine(0).GetText());
            Assert.AreEqual("dog", _textView.GetLine(1).GetText());
        }

        /// <summary>
        /// No tabs is just a column offset
        /// </summary>
        [Test]
        public void GetSpacesToColumn_NoTabs()
        {
            Create("hello world");
            Assert.AreEqual(2, _operationsRaw.GetSpacesToColumn(_textBuffer.GetLine(0), 2));
        }

        /// <summary>
        /// Tabs count as tabstop spaces
        /// </summary>
        [Test]
        public void GetSpacesToColumn_Tabs()
        {
            Create("\thello world");
            _localSettings.SetupGet(x => x.TabStop).Returns(4);
            Assert.AreEqual(5, _operationsRaw.GetSpacesToColumn(_textBuffer.GetLine(0), 2));
        }

        /// <summary>
        /// Without any tabs this should be a straight offset
        /// </summary>
        [Test]
        public void GetPointForSpaces_NoTabs()
        {
            Create("hello world");
            var point = _operationsRaw.GetPointForSpaces(_textBuffer.GetLine(0), 2);
            Assert.AreEqual(_textBuffer.GetPoint(2), point);
        }

        /// <summary>
        /// Count the tabs as a 'tabstop' value when calculating the Point
        /// </summary>
        [Test]
        public void GetPointForSpaces_Tabs()
        {
            Create("\thello world");
            _localSettings.SetupGet(x => x.TabStop).Returns(4);
            var point = _operationsRaw.GetPointForSpaces(_textBuffer.GetLine(0), 5);
            Assert.AreEqual(_textBuffer.GetPoint(2), point);
        }

        /// <summary>
        /// Verify that we properly return the new line text for the first line
        /// </summary>
        [Test]
        public void GetNewLineText_FirstLine()
        {
            Create("cat", "dog");
            Assert.AreEqual(Environment.NewLine, _operations.GetNewLineText(_textBuffer.GetPoint(0)));
        }

        /// <summary>
        /// Verify that we properly return the new line text for the first line when using a non
        /// default new line ending
        /// </summary>
        [Test]
        public void GetNewLineText_FirstLine_LineFeed()
        {
            Create("cat", "dog");
            _textBuffer.Replace(new Span(0, 0), "cat\ndog");
            Assert.AreEqual("\n", _operations.GetNewLineText(_textBuffer.GetPoint(0)));
        }

        /// <summary>
        /// Verify that we properly return the new line text for middle lines
        /// </summary>
        [Test]
        public void GetNewLineText_MiddleLine()
        {
            Create("cat", "dog", "bear");
            Assert.AreEqual(Environment.NewLine, _operations.GetNewLineText(_textBuffer.GetLine(1).Start));
        }

        /// <summary>
        /// Verify that we properly return the new line text for middle lines when using a non
        /// default new line ending
        /// </summary>
        [Test]
        public void GetNewLineText_MiddleLine_LineFeed()
        {
            Create("");
            _textBuffer.Replace(new Span(0, 0), "cat\ndog\nbear");
            Assert.AreEqual("\n", _operations.GetNewLineText(_textBuffer.GetLine(1).Start));
        }

        /// <summary>
        /// Verify that we properly return the new line text for end lines
        /// </summary>
        [Test]
        public void GetNewLineText_EndLine()
        {
            Create("cat", "dog", "bear");
            Assert.AreEqual(Environment.NewLine, _operations.GetNewLineText(_textBuffer.GetLine(2).Start));
        }

        /// <summary>
        /// Verify that we properly return the new line text for middle lines when using a non
        /// default new line ending
        /// </summary>
        [Test]
        public void GetNewLineText_EndLine_LineFeed()
        {
            Create("");
            _textBuffer.Replace(new Span(0, 0), "cat\ndog\nbear");
            Assert.AreEqual("\n", _operations.GetNewLineText(_textBuffer.GetLine(2).Start));
        }

        [Test]
        public void GoToDefinition1()
        {
            Create("foo");
            _jumpList.Setup(x => x.Add(_textView.GetCaretPoint())).Verifiable();
            _vimHost.Setup(x => x.GoToDefinition()).Returns(true);
            var res = _operations.GoToDefinition();
            Assert.IsTrue(res.IsSucceeded);
            _jumpList.Verify();
        }

        [Test]
        public void GoToDefinition2()
        {
            Create("foo");
            _vimHost.Setup(x => x.GoToDefinition()).Returns(false);
            var res = _operations.GoToDefinition();
            Assert.IsTrue(res.IsFailed);
            Assert.IsTrue(((Result.Failed)res).Item.Contains("foo"));
        }

        [Test, Description("Make sure we don't crash when nothing is under the cursor")]
        public void GoToDefinition3()
        {
            Create("      foo");
            _vimHost.Setup(x => x.GoToDefinition()).Returns(false);
            var res = _operations.GoToDefinition();
            Assert.IsTrue(res.IsFailed);
        }

        [Test]
        public void GoToDefinition4()
        {
            Create("  foo");
            _vimHost.Setup(x => x.GoToDefinition()).Returns(false);
            var res = _operations.GoToDefinition();
            Assert.IsTrue(res.IsFailed);
            Assert.AreEqual(Resources.Common_GotoDefNoWordUnderCursor, res.AsFailed().Item);
        }

        [Test]
        public void GoToDefinition5()
        {
            Create("foo bar baz");
            _vimHost.Setup(x => x.GoToDefinition()).Returns(false);
            var res = _operations.GoToDefinition();
            Assert.IsTrue(res.IsFailed);
            Assert.AreEqual(Resources.Common_GotoDefFailed("foo"), res.AsFailed().Item);
        }

        /// <summary>
        /// Simple insertion of a single item into the ITextBuffer
        /// </summary>
        [Test]
        public void Put_Single()
        {
            Create("dog", "cat");
            _operations.Put(_textView.GetLine(0).Start.Add(1), StringData.NewSimple("fish"), OperationKind.CharacterWise);
            Assert.AreEqual("dfishog", _textView.GetLine(0).GetText());
        }

        /// <summary>
        /// Put a block StringData value into the ITextBuffer over existing text
        /// </summary>
        [Test]
        public void Put_BlockOverExisting()
        {
            Create("dog", "cat");
            _operations.Put(_textView.GetLine(0).Start, VimUtil.CreateStringDataBlock("a", "b"), OperationKind.CharacterWise);
            Assert.AreEqual("adog", _textView.GetLine(0).GetText());
            Assert.AreEqual("bcat", _textView.GetLine(1).GetText());
        }

        /// <summary>
        /// Put a block StringData value into the ITextBuffer where the length of the values
        /// exceeds the number of lines in the ITextBuffer.  This will force the insert to create
        /// new lines to account for it
        /// </summary>
        [Test]
        public void Put_BlockLongerThanBuffer()
        {
            Create("dog");
            _operations.Put(_textView.GetLine(0).Start.Add(1), VimUtil.CreateStringDataBlock("a", "b"), OperationKind.CharacterWise);
            Assert.AreEqual("daog", _textView.GetLine(0).GetText());
            Assert.AreEqual(" b", _textView.GetLine(1).GetText());
        }

        /// <summary>
        /// A linewise insertion for Block should just insert each value onto a new line
        /// </summary>
        [Test]
        public void Put_BlockLineWise()
        {
            Create("dog", "cat");
            _operations.Put(_textView.GetLine(1).Start, VimUtil.CreateStringDataBlock("a", "b"), OperationKind.LineWise);
            Assert.AreEqual("dog", _textView.GetLine(0).GetText());
            Assert.AreEqual("a", _textView.GetLine(1).GetText());
            Assert.AreEqual("b", _textView.GetLine(2).GetText());
            Assert.AreEqual("cat", _textView.GetLine(3).GetText());
        }

        /// <summary>
        /// Put a single StringData instance linewise into the ITextBuffer. 
        /// </summary>
        [Test]
        public void Put_LineWiseSingleWord()
        {
            Create("cat");
            _operations.Put(_textView.GetLine(0).Start, StringData.NewSimple("fish\n"), OperationKind.LineWise);
            Assert.AreEqual("fish", _textView.GetLine(0).GetText());
            Assert.AreEqual("cat", _textView.GetLine(1).GetText());
        }

        /// <summary>
        /// Do a put at the end of the ITextBuffer which is of a single StringData and is characterwise
        /// </summary>
        [Test]
        public void Put_EndOfBufferSingleCharacterwise()
        {
            Create("cat");
            _operations.Put(_textView.GetEndPoint(), StringData.NewSimple("dog"), OperationKind.CharacterWise);
            Assert.AreEqual("catdog", _textView.GetLine(0).GetText());
        }

        /// <summary>
        /// Do a put at the end of the ITextBuffer linewise.  This is a corner case because the code has
        /// to move the final line break from the end of the StringData to the front.  Ensure that we don't
        /// keep the final \n in the inserted string because that will mess up the line count in the
        /// ITextBuffer
        /// </summary>
        [Test]
        public void Put_EndOfBufferLinewise()
        {
            Create("cat");
            _operations.Put(_textView.GetEndPoint(), StringData.NewSimple("dog\n"), OperationKind.LineWise);
            Assert.AreEqual("cat", _textView.GetLine(0).GetText());
            Assert.AreEqual("dog", _textView.GetLine(1).GetText());
            Assert.AreEqual(2, _textView.TextSnapshot.LineCount);
        }

        [Test, Description("Only shift whitespace")]
        public void ShiftLineRangeLeft1()
        {
            Create("foo");
            _operations.ShiftLineRangeLeft(_textBuffer.GetLineRange(0), 1);
            Assert.AreEqual("foo", _textBuffer.CurrentSnapshot.GetLineFromLineNumber(0).GetText());
        }

        [Test, Description("Don't puke on an empty line")]
        public void ShiftLineRangeLeft2()
        {
            Create("");
            _operations.ShiftLineRangeLeft(_textBuffer.GetLineRange(0), 1);
            Assert.AreEqual("", _textBuffer.CurrentSnapshot.GetLineFromLineNumber(0).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft3()
        {
            Create("  foo", "  bar");
            _operations.ShiftLineRangeLeft(_textBuffer.GetLineRange(0, 1), 1);
            Assert.AreEqual("foo", _textBuffer.CurrentSnapshot.GetLineFromLineNumber(0).GetText());
            Assert.AreEqual("bar", _textBuffer.CurrentSnapshot.GetLineFromLineNumber(1).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft4()
        {
            Create("   foo");
            _operations.ShiftLineRangeLeft(_textBuffer.GetLineRange(0), 1);
            Assert.AreEqual(" foo", _textBuffer.CurrentSnapshot.GetLineFromLineNumber(0).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft5()
        {
            Create("  a", "  b", "c");
            _operations.ShiftLineRangeLeft(_textBuffer.GetLineRange(0), 1);
            Assert.AreEqual("a", _textBuffer.GetLine(0).GetText());
            Assert.AreEqual("  b", _textBuffer.GetLine(1).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft6()
        {
            Create("   foo");
            _operations.ShiftLineRangeLeft(_textView.GetLineRange(0), 1);
            Assert.AreEqual(" foo", _textBuffer.GetLineRange(0).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft7()
        {
            Create(" foo");
            _operations.ShiftLineRangeLeft(_textView.GetLineRange(0), 400);
            Assert.AreEqual("foo", _textBuffer.GetLineRange(0).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft8()
        {
            Create("   foo", "    bar");
            _operations.ShiftLineRangeLeft(2);
            Assert.AreEqual(" foo", _textBuffer.GetLineRange(0).GetText());
            Assert.AreEqual("  bar", _textBuffer.GetLineRange(1).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft9()
        {
            Create(" foo", "   bar");
            _textView.MoveCaretTo(_textBuffer.GetLineRange(1).Start.Position);
            _operations.ShiftLineRangeLeft(1);
            Assert.AreEqual(" foo", _textBuffer.GetLineRange(0).GetText());
            Assert.AreEqual(" bar", _textBuffer.GetLineRange(1).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft10()
        {
            Create(" foo", "", "   bar");
            _operations.ShiftLineRangeLeft(3);
            Assert.AreEqual("foo", _textBuffer.GetLineRange(0).GetText());
            Assert.AreEqual("", _textBuffer.GetLineRange(1).GetText());
            Assert.AreEqual(" bar", _textBuffer.GetLineRange(2).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft11()
        {
            Create(" foo", "   ", "   bar");
            _operations.ShiftLineRangeLeft(3);
            Assert.AreEqual("foo", _textBuffer.GetLineRange(0).GetText());
            Assert.AreEqual(" ", _textBuffer.GetLineRange(1).GetText());
            Assert.AreEqual(" bar", _textBuffer.GetLineRange(2).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft_TabStartUsingSpaces()
        {
            Create("\tcat");
            _localSettings.SetupGet(x => x.ExpandTab).Returns(true);
            _operations.ShiftLineRangeLeft(1);
            Assert.AreEqual("  cat", _textView.GetLine(0).GetText());
        }

        [Test]
        [Description("Vim will actually normalize the line and then shift")]
        public void ShiftLineRangeLeft_MultiTabStartUsingSpaces()
        {
            Create("\t\tcat");
            _localSettings.SetupGet(x => x.ExpandTab).Returns(true);
            _operations.ShiftLineRangeLeft(1);
            Assert.AreEqual("      cat", _textView.GetLine(0).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft_TabStartUsingTabs()
        {
            Create("\tcat");
            _localSettings.SetupGet(x => x.ExpandTab).Returns(false);
            _operations.ShiftLineRangeLeft(1);
            Assert.AreEqual("  cat", _textView.GetLine(0).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft_SpaceStartUsingTabs()
        {
            Create("    cat");
            _localSettings.SetupGet(x => x.ExpandTab).Returns(false);
            _operations.ShiftLineRangeLeft(1);
            Assert.AreEqual("  cat", _textView.GetLine(0).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft_TabStartFollowedBySpacesUsingTabs()
        {
            Create("\t    cat");
            _localSettings.SetupGet(x => x.ExpandTab).Returns(false);
            _operations.ShiftLineRangeLeft(1);
            Assert.AreEqual("\t  cat", _textView.GetLine(0).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft_SpacesStartFollowedByTabFollowedBySpacesUsingTabs()
        {
            Create("    \t    cat");
            _localSettings.SetupGet(x => x.ExpandTab).Returns(false);
            _operations.ShiftLineRangeLeft(1);
            Assert.AreEqual("\t\t  cat", _textView.GetLine(0).GetText());
        }

        [Test]
        public void ShiftLineRangeLeft_SpacesStartFollowedByTabFollowedBySpacesUsingTabsWithModifiedTabStop()
        {
            Create("    \t    cat");
            _localSettings.SetupGet(x => x.ExpandTab).Returns(false);
            _localSettings.SetupGet(x => x.TabStop).Returns(2);
            _operations.ShiftLineRangeLeft(1);
            Assert.AreEqual("\t\t\t\tcat", _textView.GetLine(0).GetText());
        }
        [Test]
        public void ShiftLineRangeLeft_ShortSpacesStartFollowedByTabFollowedBySpacesUsingTabs()
        {
            Create("  \t    cat");
            _localSettings.SetupGet(x => x.ExpandTab).Returns(false);
            _operations.ShiftLineRangeLeft(1);
            Assert.AreEqual("\t  cat", _textView.GetLine(0).GetText());
        }

        [Test]
        public void ShiftLineRangeRight1()
        {
            Create("foo");
            _operations.ShiftLineRangeRight(_textBuffer.GetLineRange(0), 1);
            Assert.AreEqual("  foo", _textBuffer.CurrentSnapshot.GetLineFromLineNumber(0).GetText());
        }

        [Test]
        public void ShiftLineRangeRight2()
        {
            Create("a", "b", "c");
            _operations.ShiftLineRangeRight(_textBuffer.GetLineRange(0), 1);
            Assert.AreEqual("  a", _textBuffer.GetLine(0).GetText());
            Assert.AreEqual("b", _textBuffer.GetLine(1).GetText());
        }

        [Test]
        public void ShiftLineRangeRight3()
        {
            Create("foo");
            _operations.ShiftLineRangeRight(1);
            Assert.AreEqual("  foo", _textBuffer.GetLineRange(0).GetText());
        }

        [Test]
        public void ShiftLineRangeRight4()
        {
            Create("foo", " bar");
            _operations.ShiftLineRangeRight(2);
            Assert.AreEqual("  foo", _textBuffer.GetLineRange(0).GetText());
            Assert.AreEqual("   bar", _textBuffer.GetLineRange(1).GetText());
        }

        /// <summary>
        /// Shift the line range right starting with the second line
        /// </summary>
        [Test]
        public void ShiftLineRangeRight_SecondLine()
        {
            Create("foo", " bar");
            _textView.MoveCaretTo(_textBuffer.GetLineRange(1).Start.Position);
            _operations.ShiftLineRangeRight(1);
            Assert.AreEqual("foo", _textBuffer.GetLineRange(0).GetText());
            Assert.AreEqual("   bar", _textBuffer.GetLineRange(1).GetText());
        }

        /// <summary>
        /// Blank lines should expand when shifting right
        /// </summary>
        [Test]
        public void ShiftLineRangeRight_ExpandBlank()
        {
            Create("foo", " ", "bar");
            _operations.ShiftLineRangeRight(3);
            Assert.AreEqual("  foo", _textBuffer.GetLineRange(0).GetText());
            Assert.AreEqual("   ", _textBuffer.GetLineRange(1).GetText());
            Assert.AreEqual("  bar", _textBuffer.GetLineRange(2).GetText());
        }

        [Test]
        public void ShiftLineRangeRight_NoExpandTab()
        {
            Create("cat", "dog");
            _globalSettings.SetupGet(x => x.UseEditorSettings).Returns(false);
            _globalSettings.SetupGet(x => x.ShiftWidth).Returns(4);
            _localSettings.SetupGet(x => x.ExpandTab).Returns(false);
            _operations.ShiftLineRangeRight(1);
            Assert.AreEqual("\tcat", _textView.GetLine(0).GetText());
        }

        [Test]
        public void ShiftLineRangeRight_NoExpandTabKeepSpacesWhenFewerThanTabStop()
        {
            Create("cat", "dog");
            _globalSettings.SetupGet(x => x.UseEditorSettings).Returns(false);
            _globalSettings.SetupGet(x => x.ShiftWidth).Returns(2);
            _localSettings.SetupGet(x => x.TabStop).Returns(4);
            _localSettings.SetupGet(x => x.ExpandTab).Returns(false);
            _operations.ShiftLineRangeRight(1);
            Assert.AreEqual("  cat", _textView.GetLine(0).GetText());
        }

        [Test]
        public void ShiftLineRangeRight_SpacesStartUsingTabs()
        {
            Create("  cat", "dog");
            _globalSettings.SetupGet(x => x.UseEditorSettings).Returns(false);
            _localSettings.SetupGet(x => x.ExpandTab).Returns(false);
            _localSettings.SetupGet(x => x.TabStop).Returns(2);
            _operations.ShiftLineRangeRight(1);
            Assert.AreEqual("\t\tcat", _textView.GetLine(0).GetText());
        }

        /// <summary>
        /// Make sure it shifts on the appropriate column and not column 0
        /// </summary>
        [Test]
        public void ShiftLineBlockRight_Simple()
        {
            Create("cat", "dog");
            _operations.ShiftLineBlockRight(_textView.GetBlock(column: 1, length: 1, startLine: 0, lineCount: 2), 1);
            Assert.AreEqual("c  at", _textView.GetLine(0).GetText());
            Assert.AreEqual("d  og", _textView.GetLine(1).GetText());
        }

        /// <summary>
        /// Make sure it shifts on the appropriate column and not column 0
        /// </summary>
        [Test]
        public void ShiftLineBlockLeft_Simple()
        {
            Create("c  at", "d  og");
            _operations.ShiftLineBlockLeft(_textView.GetBlock(column: 1, length: 1, startLine: 0, lineCount: 2), 1);
            Assert.AreEqual("cat", _textView.GetLine(0).GetText());
            Assert.AreEqual("dog", _textView.GetLine(1).GetText());
        }

        /// <summary>
        /// Make sure the caret column is maintained when specified going down
        /// </summary>
        [Test]
        public void MaintainCaretColumn_Down()
        {
            Create("the dog chased the ball", "hello", "the cat climbed the tree");
            var motionResult = VimUtil.CreateMotionResult(
                _textView.GetLineRange(0, 1).ExtentIncludingLineBreak,
                motionKind: MotionKind.NewLineWise(CaretColumn.NewInLastLine(2)),
                flags: MotionResultFlags.MaintainCaretColumn);
            _operations.MoveCaretToMotionResult(motionResult);
            Assert.AreEqual(2, _operationsRaw.MaintainCaretColumn.Value);
        }

        /// <summary>
        /// Don't maintain the caret column if the maintain flag is not specified
        /// </summary>
        [Test]
        public void MaintainCaretColumn_IgnoreIfFlagNotSpecified()
        {
            Create("the dog chased the ball", "hello", "the cat climbed the tree");
            var motionResult = VimUtil.CreateMotionResult(
                _textView.GetLineRange(0, 1).ExtentIncludingLineBreak,
                motionKind: MotionKind.NewLineWise(CaretColumn.NewInLastLine(2)),
                flags: MotionResultFlags.None);
            var data = VimUtil.CreateMotionResult(
                new SnapshotSpan(_textBuffer.CurrentSnapshot, 1, 2),
                true,
                MotionKind.CharacterWiseInclusive);
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(2, _textView.GetCaretPoint().Position);
        }

        [Test]
        public void MoveCaretToMotionResult2()
        {
            Create("foo", "bar", "baz");
            var data = VimUtil.CreateMotionResult(
                new SnapshotSpan(_textBuffer.CurrentSnapshot, 0, 1),
                true,
                MotionKind.CharacterWiseInclusive);
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(0, _textView.GetCaretPoint().Position);
        }

        [Test]
        public void MoveCaretToMotionResult3()
        {
            Create("foo", "bar", "baz");
            var data = VimUtil.CreateMotionResult(
                new SnapshotSpan(_textBuffer.CurrentSnapshot, 0, 0),
                true,
                MotionKind.CharacterWiseInclusive);
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(0, _textView.GetCaretPoint().Position);
        }

        [Test]
        public void MoveCaretToMotionResult4()
        {
            Create("foo", "bar", "baz");
            var data = VimUtil.CreateMotionResult(
                new SnapshotSpan(_textBuffer.CurrentSnapshot, 0, 3),
                false,
                MotionKind.CharacterWiseInclusive);
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(0, _textView.GetCaretPoint().Position);
        }

        [Test]
        public void MoveCaretToMotionResult6()
        {
            Create("foo", "bar", "baz");
            var data = VimUtil.CreateMotionResult(
                new SnapshotSpan(_textBuffer.CurrentSnapshot, 0, 1),
                true,
                MotionKind.CharacterWiseExclusive);
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(1, _textView.GetCaretPoint().Position);
        }

        [Test]
        [Description("Motion to empty last line")]
        public void MoveCaretToMotionResult7()
        {
            Create("foo", "bar", "");
            var data = VimUtil.CreateMotionResult(
                new SnapshotSpan(_textBuffer.CurrentSnapshot, 0, _textBuffer.CurrentSnapshot.Length),
                true,
                MotionKind.NewLineWise(CaretColumn.None));
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(2, _textView.GetCaretPoint().GetContainingLine().LineNumber);
        }

        [Test]
        [Description("Need to respect the specified column")]
        public void MoveCaretToMotionResult8()
        {
            Create("foo", "bar", "");
            var data = VimUtil.CreateMotionResult(
                _textBuffer.GetLineRange(0, 1).Extent,
                true,
                MotionKind.NewLineWise(CaretColumn.NewInLastLine(1)));
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(Tuple.Create(1, 1), SnapshotPointUtil.GetLineColumn(_textView.GetCaretPoint()));
        }

        [Test]
        [Description("Ignore column if it's past the end of the line")]
        public void MoveCaretToMotionResult9()
        {
            Create("foo", "bar", "");
            var data = VimUtil.CreateMotionResult(
                _textBuffer.GetLineRange(0, 1).Extent,
                true,
                MotionKind.NewLineWise(CaretColumn.NewInLastLine(100)));
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(Tuple.Create(1, 2), SnapshotPointUtil.GetLineColumn(_textView.GetCaretPoint()));
        }

        [Test]
        [Description("Need to respect the specified column")]
        public void MoveCaretToMotionResult10()
        {
            Create("foo", "bar", "");
            var data = VimUtil.CreateMotionResult(
                _textBuffer.GetLineRange(0, 1).Extent,
                true,
                MotionKind.NewLineWise(CaretColumn.NewInLastLine(0)));
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(Tuple.Create(1, 0), SnapshotPointUtil.GetLineColumn(_textView.GetCaretPoint()));
        }

        [Test]
        [Description("Reverse spans should move to the start of the span")]
        public void MoveCaretToMotionResult11()
        {
            Create("dog", "cat", "bear");
            var data = VimUtil.CreateMotionResult(
                _textBuffer.GetLineRange(0, 1).Extent,
                false,
                MotionKind.CharacterWiseInclusive);
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(Tuple.Create(0, 0), SnapshotPointUtil.GetLineColumn(_textView.GetCaretPoint()));
        }

        [Test]
        [Description("Reverse spans should move to the start of the span and respect column")]
        public void MoveCaretToMotionResult12()
        {
            Create("dog", "cat", "bear");
            var data = VimUtil.CreateMotionResult(
                _textBuffer.GetLineRange(0, 1).ExtentIncludingLineBreak,
                false,
                MotionKind.NewLineWise(CaretColumn.NewInLastLine(2)));
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(Tuple.Create(0, 2), SnapshotPointUtil.GetLineColumn(_textView.GetCaretPoint()));
        }

        [Test]
        [Description("Exclusive spans going backward should go through normal movements")]
        public void MoveCaretToMotionResult14()
        {
            Create("dog", "cat", "bear");
            var data = VimUtil.CreateMotionResult(
                _textBuffer.GetLineRange(0, 1).ExtentIncludingLineBreak,
                false,
                MotionKind.CharacterWiseExclusive);
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(_textBuffer.GetLine(0).Start, _textView.GetCaretPoint());
        }

        [Test]
        [Description("Used with the - motion")]
        public void MoveCaretToMotionResult_ReverseLineWiseWithColumn()
        {
            Create(" dog", "cat", "bear");
            var data = VimUtil.CreateMotionResult(
                span: _textView.GetLineRange(0, 1).ExtentIncludingLineBreak,
                isForward: false,
                motionKind: MotionKind.NewLineWise(CaretColumn.NewInLastLine(1)));
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(1, _textView.GetCaretPoint().Position);
        }

        /// <summary>
        /// Spans going forward which have the AfterLastLine value should have the caret after the 
        /// last line
        /// </summary>
        [Test]
        public void MoveCaretToMotionResult_CaretAfterLastLine()
        {
            Create("dog", "cat", "bear");
            var data = VimUtil.CreateMotionResult(
                _textBuffer.GetLineRange(0).ExtentIncludingLineBreak,
                true,
                MotionKind.NewLineWise(CaretColumn.AfterLastLine));
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(_textBuffer.GetLine(1).Start, _textView.GetCaretPoint());
        }

        /// <summary>
        /// Exclusive motions should not go to the end if it puts them into virtual space and 
        /// we don't have 've=onemore'
        /// </summary>
        [Test]
        public void MoveCaretToMotionResult_InVirtualSpaceWithNoVirtualEdit()
        {
            Create("foo", "bar", "baz");
            var data = VimUtil.CreateMotionResult(
                new SnapshotSpan(_textBuffer.CurrentSnapshot, 1, 2),
                true,
                MotionKind.CharacterWiseExclusive);
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(2, _textView.GetCaretPoint().Position);
        }

        /// <summary>
        /// An exclusive selection should cause inclusive motions to be treated as
        /// if they were exclusive for caret movement
        /// </summary>
        [Test]
        public void MoveCaretToMotionResult_InclusiveWithExclusiveSelection()
        {
            Create("the dog");
            _globalSettings.SetupGet(x => x.SelectionKind).Returns(SelectionKind.Exclusive);
            _vimTextBuffer.SetupGet(x => x.ModeKind).Returns(ModeKind.VisualBlock);
            var data = VimUtil.CreateMotionResult(_textBuffer.GetSpan(0, 3), motionKind: MotionKind.CharacterWiseInclusive);
            _operations.MoveCaretToMotionResult(data);
            Assert.AreEqual(3, _textView.GetCaretPoint().Position);
        }

        [Test]
        public void Beep1()
        {
            Create(String.Empty);
            _globalSettings.Setup(x => x.VisualBell).Returns(false).Verifiable();
            _vimHost.Setup(x => x.Beep()).Verifiable();
            _operations.Beep();
            _factory.Verify();
        }

        [Test]
        public void Beep2()
        {
            Create(String.Empty);
            _globalSettings.Setup(x => x.VisualBell).Returns(true).Verifiable();
            _operations.Beep();
            _factory.Verify();
        }

        [Test, Description("Only once per line")]
        public void Substitute1()
        {
            Create("bar bar", "foo");
            _operations.Substitute("bar", "again", _textView.GetLineRange(0), SubstituteFlags.None);
            Assert.AreEqual("again bar", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
            Assert.AreEqual("foo", _textView.TextSnapshot.GetLineFromLineNumber(1).GetText());
        }

        [Test, Description("Should run on every line in the span")]
        public void Substitute2()
        {
            Create("bar bar", "foo bar");
            _statusUtil.Setup(x => x.OnStatus(Resources.Common_SubstituteComplete(2, 2))).Verifiable();
            _operations.Substitute("bar", "again", _textView.GetLineRange(0, 1), SubstituteFlags.None);
            Assert.AreEqual("again bar", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
            Assert.AreEqual("foo again", _textView.TextSnapshot.GetLineFromLineNumber(1).GetText());
            _statusUtil.Verify();
        }

        [Test, Description("Replace all if the option is set")]
        public void Substitute3()
        {
            Create("bar bar", "foo bar");
            _statusUtil.Setup(x => x.OnStatus(Resources.Common_SubstituteComplete(2, 1))).Verifiable();
            _operations.Substitute("bar", "again", _textView.GetLineRange(0), SubstituteFlags.ReplaceAll);
            Assert.AreEqual("again again", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
            Assert.AreEqual("foo bar", _textView.TextSnapshot.GetLineFromLineNumber(1).GetText());
            _statusUtil.Verify();
        }

        [Test, Description("Ignore case")]
        public void Substitute4()
        {
            Create("bar bar", "foo bar");
            _operations.Substitute("BAR", "again", _textView.GetLineRange(0), SubstituteFlags.IgnoreCase);
            Assert.AreEqual("again bar", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
        }

        [Test, Description("Ignore case and replace all")]
        public void Substitute5()
        {
            Create("bar bar", "foo bar");
            _statusUtil.Setup(x => x.OnStatus(Resources.Common_SubstituteComplete(2, 1))).Verifiable();
            _operations.Substitute("BAR", "again", _textView.GetLineRange(0), SubstituteFlags.IgnoreCase | SubstituteFlags.ReplaceAll);
            Assert.AreEqual("again again", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
            _statusUtil.Verify();
        }

        [Test, Description("Ignore case and replace all")]
        public void Substitute6()
        {
            Create("bar bar", "foo bar");
            _statusUtil.Setup(x => x.OnStatus(Resources.Common_SubstituteComplete(2, 1))).Verifiable();
            _operations.Substitute("BAR", "again", _textView.GetLineRange(0), SubstituteFlags.IgnoreCase | SubstituteFlags.ReplaceAll);
            Assert.AreEqual("again again", _textView.TextSnapshot.GetLineFromLineNumber(0).GetText());
            _statusUtil.Verify();
        }

        [Test, Description("No matches")]
        public void Substitute7()
        {
            Create("bar bar", "foo bar");
            var pattern = "BAR";
            _statusUtil.Setup(x => x.OnError(Resources.Common_PatternNotFound(pattern))).Verifiable();
            _operations.Substitute("BAR", "again", _textView.GetLineRange(0), SubstituteFlags.OrdinalCase);
            _statusUtil.Verify();
        }

        [Test, Description("Invalid regex")]
        public void Substitute8()
        {
            Create("bar bar", "foo bar");
            var original = _textView.TextSnapshot;
            var pattern = "(foo";
            _statusUtil.Setup(x => x.OnError(Resources.Common_PatternNotFound(pattern))).Verifiable();
            _operations.Substitute(pattern, "again", _textView.GetLineRange(0), SubstituteFlags.OrdinalCase);
            _statusUtil.Verify();
            Assert.AreSame(original, _textView.TextSnapshot);
        }

        [Test, Description("Report only shouldn't make any changes")]
        public void Substitute9()
        {
            Create("bar bar", "foo bar");
            var tss = _textView.TextSnapshot;
            _statusUtil.Setup(x => x.OnStatus(Resources.Common_SubstituteComplete(2, 1))).Verifiable();
            _operations.Substitute("bar", "again", _textView.GetLineRange(0), SubstituteFlags.ReplaceAll | SubstituteFlags.ReportOnly);
            _statusUtil.Verify();
            Assert.AreSame(tss, _textView.TextSnapshot);
        }

        [Test, Description("No matches and report only")]
        public void Substitute10()
        {
            Create("bar bar", "foo bar");
            var tss = _textView.TextSnapshot;
            var pattern = "BAR";
            _operations.Substitute(pattern, "again", _textView.GetLineRange(0), SubstituteFlags.OrdinalCase | SubstituteFlags.ReportOnly);
        }

        [Test]
        [Description("Across multiple lines one match per line should be processed")]
        public void Substitute11()
        {
            Create("cat", "bat");
            _statusUtil.Setup(x => x.OnStatus(Resources.Common_SubstituteComplete(2, 2))).Verifiable();
            _operations.Substitute("a", "o", _textView.GetLineRange(0, 1), SubstituteFlags.None);
            Assert.AreEqual("cot", _textView.GetLine(0).GetText());
            Assert.AreEqual("bot", _textView.GetLine(1).GetText());
        }

        [Test]
        [Description("Respect the magic flag")]
        public void Substitute12()
        {
            Create("cat", "bat");
            _globalSettings.SetupGet(x => x.Magic).Returns(false);
            _operations.Substitute(".", "b", _textView.GetLineRange(0, 0), SubstituteFlags.Magic);
            Assert.AreEqual("bat", _textView.GetLine(0).GetText());
        }

        [Test]
        [Description("Respect the nomagic flag")]
        public void Substitute13()
        {
            Create("cat.", "bat");
            _globalSettings.SetupGet(x => x.Magic).Returns(true);
            _operations.Substitute(".", "s", _textView.GetLineRange(0, 0), SubstituteFlags.Nomagic);
            Assert.AreEqual("cats", _textView.GetLine(0).GetText());
        }

        [Test]
        [Description("Don't error when the pattern is not found if SuppressErrors is passed")]
        public void Substitute14()
        {
            Create("cat", "bat");
            _operations.Substitute("z", "b", _textView.GetLineRange(0, 0), SubstituteFlags.SuppressError);
            _factory.Verify();
        }


        [Test]
        public void GoToGlobalDeclaration1()
        {
            Create("foo bar");
            _vimHost.Setup(x => x.GoToGlobalDeclaration(_textView, "foo")).Returns(true).Verifiable();
            _operations.GoToGlobalDeclaration();
            _vimHost.Verify();
        }

        [Test]
        public void GoToGlobalDeclaration2()
        {
            Create("foo bar");
            _vimHost.Setup(x => x.GoToGlobalDeclaration(_textView, "foo")).Returns(false).Verifiable();
            _vimHost.Setup(x => x.Beep()).Verifiable();
            _operations.GoToGlobalDeclaration();
            _vimHost.Verify();
        }

        [Test]
        public void GoToLocalDeclaration1()
        {
            Create("foo bar");
            _vimHost.Setup(x => x.GoToLocalDeclaration(_textView, "foo")).Returns(true).Verifiable();
            _operations.GoToLocalDeclaration();
            _vimHost.Verify();
        }

        [Test]
        public void GoToLocalDeclaration2()
        {
            Create("foo bar");
            _vimHost.Setup(x => x.GoToLocalDeclaration(_textView, "foo")).Returns(false).Verifiable();
            _vimHost.Setup(x => x.Beep()).Verifiable();
            _operations.GoToLocalDeclaration();
            _vimHost.Verify();
        }

        [Test]
        public void GoToFile1()
        {
            Create("foo bar");
            _vimHost.Setup(x => x.IsDirty(_textBuffer)).Returns(false).Verifiable();
            _vimHost.Setup(x => x.LoadFileIntoExistingWindow("foo", _textView)).Returns(HostResult.Success).Verifiable();
            _operations.GoToFile();
            _vimHost.Verify();
        }

        [Test]
        public void GoToFile2()
        {
            Create("foo bar");
            _vimHost.Setup(x => x.IsDirty(_textBuffer)).Returns(false).Verifiable();
            _vimHost.Setup(x => x.LoadFileIntoExistingWindow("foo", _textView)).Returns(HostResult.NewError("")).Verifiable();
            _statusUtil.Setup(x => x.OnError(Resources.NormalMode_CantFindFile("foo"))).Verifiable();
            _operations.GoToFile();
            _statusUtil.Verify();
            _vimHost.Verify();
        }

        /// <summary>
        /// If there is no match anywhere in the ITextBuffer raise the appropriate message
        /// </summary>
        [Test]
        public void RaiseSearchResultMessages_NoMatch()
        {
            Create("");
            _statusUtil.Setup(x => x.OnError(Resources.Common_PatternNotFound("dog"))).Verifiable();
            _operations.RaiseSearchResultMessage(SearchResult.NewNotFound(
                VimUtil.CreateSearchData("dog"),
                false));
            _statusUtil.Verify();
        }

        /// <summary>
        /// If the match is not found but would be found if we enabled wrapping then raise
        /// a different message
        /// </summary>
        [Test]
        public void RaiseSearchResultMessages_NoMatchInPathForward()
        {
            Create("");
            _statusUtil.Setup(x => x.OnError(Resources.Common_SearchHitBottomWithout("dog"))).Verifiable();
            _operations.RaiseSearchResultMessage(SearchResult.NewNotFound(
                VimUtil.CreateSearchData("dog", SearchKind.Forward),
                true));
            _statusUtil.Verify();
        }

        /// <summary>
        /// If the match is not found but would be found if we enabled wrapping then raise
        /// a different message
        /// </summary>
        [Test]
        public void RaiseSearchResultMessages_NoMatchInPathBackward()
        {
            Create("");
            _statusUtil.Setup(x => x.OnError(Resources.Common_SearchHitTopWithout("dog"))).Verifiable();
            _operations.RaiseSearchResultMessage(SearchResult.NewNotFound(
                VimUtil.CreateSearchData("dog", SearchKind.Backward),
                true));
            _statusUtil.Verify();
        }

        /// <summary>
        /// Make sure that editor indent trumps 'autoindent'
        /// </summary>
        [Test]
        public void GetNewLineIndent_EditorTrumpsAutoIndent()
        {
            Create("cat", "dog", "");
            _globalSettings.SetupGet(x => x.UseEditorIndent).Returns(true);
            _smartIndentationService.Setup(x => x.GetDesiredIndentation(_textView, It.IsAny<ITextSnapshotLine>())).Returns(8);
            var indent = _operations.GetNewLineIndent(_textView.GetLine(1), _textView.GetLine(2));
            Assert.AreEqual(8, indent.Value);
        }

        /// <summary>
        /// Use Vim settings if the 'useeditorindent' setting is not present
        /// </summary>
        [Test]
        public void GetNewLineIndent_RevertToVimIndentIfEditorIndentFails()
        {
            Create("  cat", "  dog", "");
            _globalSettings.SetupGet(x => x.UseEditorIndent).Returns(false);
            _localSettings.SetupGet(x => x.AutoIndent).Returns(true);
            _smartIndentationService.Setup(x => x.GetDesiredIndentation(_textView, It.IsAny<ITextSnapshotLine>())).Returns((int?)null);
            var indent = _operations.GetNewLineIndent(_textView.GetLine(1), _textView.GetLine(2));
            Assert.AreEqual(2, indent.Value);
        }
    }
}
