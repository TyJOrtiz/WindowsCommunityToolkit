// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Deferred;
using Windows.System;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace Microsoft.Toolkit.Uwp.UI.Controls
{
    [TemplatePart(Name = PartRichEditBox, Type = typeof(RichEditBox))]
    [TemplatePart(Name = PartSuggestionsPopup, Type = typeof(Popup))]
    [TemplatePart(Name = PartSuggestionsList, Type = typeof(ListViewBase))]
    public partial class RichSuggestBox : ItemsControl
    {
        private const string PartRichEditBox = "RichEditBox";
        private const string PartSuggestionsPopup = "SuggestionsPopup";
        private const string PartSuggestionsList = "SuggestionsList";

        private Popup _suggestionPopup;
        private RichEditBox _richEditBox;
        private ListViewBase _suggestionsList;

        private int _suggestionChoice;
        private string _currentQuery;
        private string _currentPrefix;
        private bool _ignoreChange;
        private ITextRange _currentRange;
        private CancellationTokenSource _suggestionRequestedTokenSource;
        private readonly Dictionary<string, SuggestionInfo> _tokens;
        private readonly ObservableCollection<SuggestionInfo> _visibleTokens;

        /// <summary>
        /// Initializes a new instance of the <see cref="RichSuggestBox"/> class.
        /// </summary>
        public RichSuggestBox()
        {
            _tokens = new Dictionary<string, SuggestionInfo>();
            _visibleTokens = new ObservableCollection<SuggestionInfo>();
            Tokens = new ReadOnlyObservableCollection<SuggestionInfo>(_visibleTokens);
            LockObj = new object();

            DefaultStyleKey = typeof(RichSuggestBox);

            RegisterPropertyChangedCallback(ItemsSourceProperty, ItemsSource_PropertyChanged);
        }

        public void ClearUndoRedoSuggestionHistory()
        {
            TextDocument.ClearUndoRedoHistory();
            if (_tokens.Count == 0)
            {
                return;
            }

            var keysToDelete = _tokens.Where(pair => !pair.Value.Active).Select(pair => pair.Key).ToArray();
            foreach (var key in keysToDelete)
            {
                _tokens.Remove(key);
            }
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _suggestionPopup = (Popup)GetTemplateChild(PartSuggestionsPopup);
            _richEditBox = (RichEditBox)GetTemplateChild(PartRichEditBox);
            _suggestionsList = (ListViewBase)GetTemplateChild(PartSuggestionsList);

            _richEditBox.SizeChanged += RichEditBox_SizeChanged;
            _richEditBox.TextChanging += RichEditBox_TextChanging;
            _richEditBox.TextChanged += RichEditBox_TextChanged;
            _richEditBox.SelectionChanging += RichEditBox_SelectionChanging;
            _richEditBox.SelectionChanged += RichEditBox_SelectionChanged;
            _richEditBox.AddHandler(PointerPressedEvent, new PointerEventHandler(RichEditBoxPointerEventHandler), true);
            AddKeyboardAccelerators();
            _suggestionsList.ItemClick += SuggestionsList_ItemClick;

            _suggestionsList.GotFocus += (sender, args) => _richEditBox.Focus(FocusState.Programmatic);
            LostFocus += (sender, args) => ShowSuggestionsPopup(false);
        }

        private static void OnPrefixesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (RichSuggestBox)d;

            var newValue = (string)e.NewValue;
            var prefixes = EnforcePrefixesRequirements(newValue);

            if (newValue != prefixes)
            {
                view.SetValue(PrefixesProperty, prefixes);
            }
        }

        private static string EnforcePrefixesRequirements(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "@";
            }

            var possibles = string.Concat(value.Where(char.IsPunctuation));
            return string.IsNullOrEmpty(possibles) ? "@" : possibles;
        }

        private void RichEditBox_SelectionChanging(RichEditBox sender, RichEditBoxSelectionChangingEventArgs args)
        {
            TextDocument.BeginUndoGroup();

            var selection = TextDocument.Selection;
            if (selection.Type != SelectionType.InsertionPoint)
            {
                return;
            }

            var range = selection.GetClone();
            range.Expand(TextRangeUnit.Link);
            if (!_tokens.ContainsKey(range.Link))
            {
                return;
            }

            if (range.StartPosition < selection.StartPosition && selection.EndPosition < range.EndPosition)
            {
                // Prevent user from manually editing the link
                selection.SetRange(range.StartPosition, range.EndPosition);
            }
            else if (selection.StartPosition == range.StartPosition)
            {
                // Reset formatting if selection is sandwiched between 2 adjacent links
                // or if the link is at the beginning of the document
                range.MoveStart(TextRangeUnit.Link, -1);
                if (selection.StartPosition != range.StartPosition || selection.StartPosition == 0)
                {
                    ApplyDefaultFormatToRange(selection);
                }
            }
        }

        private async void RichEditBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            await RequestForSuggestionsAsync();
        }

        private void RichEditBoxPointerEventHandler(object sender, PointerRoutedEventArgs e)
        {
            ShowSuggestionsPopup(false);
        }

        private void AddKeyboardAccelerators()
        {
            var enterKeyAccelerator = new KeyboardAccelerator { Key = VirtualKey.Enter };
            var downKeyAccelerator = new KeyboardAccelerator { Key = VirtualKey.Down };
            var upKeyAccelerator = new KeyboardAccelerator { Key = VirtualKey.Up };
            var escapeKeyAccelerator = new KeyboardAccelerator { Key = VirtualKey.Escape };

            enterKeyAccelerator.Invoked += RichEditBoxKeyboardAccelerator_Invoked;
            downKeyAccelerator.Invoked += RichEditBoxKeyboardAccelerator_Invoked;
            upKeyAccelerator.Invoked += RichEditBoxKeyboardAccelerator_Invoked;
            escapeKeyAccelerator.Invoked += RichEditBoxKeyboardAccelerator_Invoked;

            _richEditBox.KeyboardAccelerators.Add(enterKeyAccelerator);
            _richEditBox.KeyboardAccelerators.Add(downKeyAccelerator);
            _richEditBox.KeyboardAccelerators.Add(upKeyAccelerator);
            _richEditBox.KeyboardAccelerators.Add(escapeKeyAccelerator);
        }

        private void RichEditBoxKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            var itemsList = _suggestionsList.Items;
            if (!_suggestionPopup.IsOpen || itemsList == null || itemsList.Count == 0)
            {
                return;
            }

            var key = args.KeyboardAccelerator.Key;
            switch (key)
            {
                case VirtualKey.Up when itemsList.Count == 1:
                case VirtualKey.Down when itemsList.Count == 1:
                    {
                        _suggestionsList.SelectedItem = itemsList[0];
                        break;
                    }
                case VirtualKey.Up:
                    {
                        _suggestionChoice = _suggestionChoice <= 0 ? itemsList.Count : _suggestionChoice - 1;
                        _suggestionsList.SelectedItem = _suggestionChoice == 0 ? null : itemsList[_suggestionChoice - 1];
                        args.Handled = true;
                        break;
                    }
                case VirtualKey.Down:
                    {
                        _suggestionChoice = _suggestionChoice >= itemsList.Count ? 0 : _suggestionChoice + 1;
                        _suggestionsList.SelectedItem = _suggestionChoice == 0 ? null : itemsList[_suggestionChoice - 1];
                        args.Handled = true;
                        break;
                    }
                case VirtualKey.Enter when _suggestionsList.SelectedItem != null:
                    {
                        ShowSuggestionsPopup(false);
                        _ = OnSuggestionSelectedAsync(_suggestionsList.SelectedItem);
                        args.Handled = true;
                        break;
                    }
                case VirtualKey.Escape:
                    {
                        ShowSuggestionsPopup(false);
                        args.Handled = true;
                        break;
                    }
            }
        }

        private async void SuggestionsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var selectedItem = e.ClickedItem;
            await OnSuggestionSelectedAsync(selectedItem);
        }

        private void RichEditBox_TextChanging(RichEditBox sender, RichEditBoxTextChangingEventArgs args)
        {
            if (_ignoreChange)
            {
                return;
            }

            _ignoreChange = true;
            ValidateTokensInDocument();
            TextDocument.EndUndoGroup();
            TextDocument.BeginUndoGroup();
            _ignoreChange = false;
        }

        private void RichEditBox_TextChanged(object sender, RoutedEventArgs e)
        {
            TextChanged?.Invoke((RichEditBox)sender, e);
            UpdateVisibleTokenList();
        }

        private void RichEditBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _suggestionPopup.VerticalOffset = e.NewSize.Height;
            _suggestionsList.MaxWidth = e.NewSize.Width;
        }

        private void ItemsSource_PropertyChanged(DependencyObject sender, DependencyProperty dp)
        {
            _suggestionChoice = 0;
            ShowSuggestionsPopup(ItemsSource is IEnumerable);
        }

        private async Task RequestForSuggestionsAsync()
        {
            _suggestionRequestedTokenSource?.Cancel();

            if (TryExtractQueryFromSelection(out var prefix, out var query, out var range) &&
                SuggestionsRequested != null)
            {
                _suggestionRequestedTokenSource = new CancellationTokenSource();
                _currentPrefix = prefix;
                _currentQuery = query;
                _currentRange = range;

                var cancellationToken = _suggestionRequestedTokenSource.Token;
                var eventArgs = new SuggestionsRequestedEventArgs { Query = query, Prefix = prefix };
                try
                {
                    await SuggestionsRequested.InvokeAsync(this, eventArgs, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    eventArgs.Cancel = true;
                }
            }
            else
            {
                ShowSuggestionsPopup(false);
            }
        }

        private async Task OnSuggestionSelectedAsync(object selectedItem)
        {
            var range = _currentRange;
            var id = Guid.NewGuid();
            var prefix = _currentPrefix;
            var query = _currentQuery;

            // range has length of 0 at the end of the commit.
            // Checking length == 0 to avoid committing twice.
            if (SuggestionChosen == null || prefix == null || query == null || range == null ||
                range.Length == 0)
            {
                return;
            }

            var eventArgs = new SuggestionChosenEventArgs
            {
                Id = id,
                Prefix = prefix,
                Query = query,
                SelectedItem = selectedItem,
                Format = CreateSuggestionTokenFormat()
            };
            var textBefore = range.Text;
            await SuggestionChosen.InvokeAsync(this, eventArgs);
            var text = eventArgs.Text;

            // Since this operation is async, the document may have changed at this point.
            // Double check if the range still has the expected query.
            if (string.IsNullOrEmpty(text) || textBefore != range.Text ||
                !TryExtractQueryFromRange(range, out var testPrefix, out var testQuery) ||
                testPrefix != prefix || testQuery != query)
            {
                return;
            }

            lock (LockObj)
            {
                var displayText = prefix + text;
                var tokenRange = CommitSuggestionIntoDocument(range, displayText, id, eventArgs.Format);

                var token = new SuggestionInfo(id, displayText, this) { Active = true, Item = selectedItem };
                token.UpdateTextRange(tokenRange);
                _tokens.Add(tokenRange.Link, token);
            }
        }

        private ITextRange CommitSuggestionIntoDocument(ITextRange range, string displayText, Guid id, SuggestionTokenFormat format)
        {
            _ignoreChange = true;
            TextDocument.BeginUndoGroup();

            range.SetText(TextSetOptions.Unhide, displayText);
            range.Link = $"\"{id}\"";

            range.CharacterFormat.BackgroundColor = format.Background;
            range.CharacterFormat.ForegroundColor = format.Foreground;
            range.CharacterFormat.Bold = format.Bold;
            range.CharacterFormat.Italic = format.Italic;
            range.CharacterFormat.Underline = format.Underline;

            var returnRange = TextDocument.GetRange(range.StartPosition, range.EndPosition);

            range.Collapse(false);
            range.SetText(TextSetOptions.Unhide, " ");
            range.Collapse(false);
            TextDocument.Selection.SetRange(range.EndPosition, range.EndPosition);

            TextDocument.EndUndoGroup();
            _ignoreChange = false;
            return returnRange;
        }

        private void ValidateTokensInDocument()
        {
            foreach (var (_, token) in _tokens)
            {
                token.Active = false;
            }

            var range = TextDocument.GetRange(0, 0);
            range.SetIndex(TextRangeUnit.Character, -1, false);

            // Handle link at the very end of the document where GetIndex fails to detect
            range.Expand(TextRangeUnit.Link);
            ValidateTokenFromRange(range);

            var nextIndex = range.GetIndex(TextRangeUnit.Link);
            while (nextIndex != 0 && nextIndex != 1)
            {
                range.Move(TextRangeUnit.Link, -1);

                var validateRange = range.GetClone();
                validateRange.Expand(TextRangeUnit.Link);

                // Adjacent links have the same index. Manually check each link with Collapse and Expand.
                var previousText = validateRange.Text;
                var hasAdjacentToken = true;
                while (hasAdjacentToken)
                {
                    ValidateTokenFromRange(validateRange);
                    validateRange.Collapse(false);
                    validateRange.Expand(TextRangeUnit.Link);

                    hasAdjacentToken = validateRange.Text != previousText;
                    previousText = validateRange.Text;
                }

                nextIndex = range.GetIndex(TextRangeUnit.Link);
            }
        }

        private bool ValidateTokenFromRange(ITextRange range)
        {
            if (range.Length == 0 || string.IsNullOrEmpty(range.Link) ||
                !_tokens.TryGetValue(range.Link, out var token))
            {
                // Handle case where range.Link is empty but it still recognized and rendered as a link
                if (range.CharacterFormat.LinkType == LinkType.FriendlyLinkName)
                {
                    range.Link = string.Empty;
                }
                return false;
            }

            if (token.ToString() != range.Text)
            {
                //range.Link = string.Empty;
                range.CharacterFormat = TextDocument.GetDefaultCharacterFormat();
                token.Active = false;
                return false;
            }

            token.UpdateTextRange(range);
            token.Active = true;
            return true;
        }

        private void ShowSuggestionsPopup(bool show)
        {
            _suggestionPopup.IsOpen = show;
            if (!show)
            {
                _suggestionChoice = 0;
            }
        }

        private bool TryExtractQueryFromSelection(out string prefix, out string query, out ITextRange range)
        {
            prefix = string.Empty;
            query = string.Empty;
            range = null;
            if (TextDocument.Selection.Type != SelectionType.InsertionPoint)
            {
                return false;
            }

            // Check if selection is on existing link (suggestion)
            var expandCount = TextDocument.Selection.GetClone().Expand(TextRangeUnit.Link);
            if (expandCount != 0)
            {
                return false;
            }

            var selection = TextDocument.Selection.GetClone();
            selection.MoveStart(TextRangeUnit.Word, -1);
            if (selection.Length == 0)
            {
                return false;
            }

            range = selection;
            if (TryExtractQueryFromRange(selection, out prefix, out query))
            {
                return true;
            }

            selection.MoveStart(TextRangeUnit.Word, -1);
            if (TryExtractQueryFromRange(selection, out prefix, out query))
            {
                return true;
            }

            range = null;
            return false;
        }

        private bool TryExtractQueryFromRange(ITextRange range, out string prefix, out string query)
        {
            prefix = string.Empty;
            query = string.Empty;
            range.GetText(TextGetOptions.NoHidden, out var possibleQuery);
            if (possibleQuery.Length > 0 && Prefixes.Contains(possibleQuery[0]) &&
                !possibleQuery.Any(char.IsWhiteSpace) && string.IsNullOrEmpty(range.Link))
            {
                if (possibleQuery.Length == 1)
                {
                    prefix = possibleQuery;
                    return true;
                }

                prefix = possibleQuery[0].ToString();
                query = possibleQuery.Substring(1);
                return true;
            }

            return false;
        }

        private SuggestionTokenFormat CreateSuggestionTokenFormat()
        {
            var defaultFormat = TextDocument.GetDefaultCharacterFormat();
            if (SuggestionBackground != null)
            {
                defaultFormat.BackgroundColor = SuggestionBackground.Color;
            }

            if (SuggestionForeground != null)
            {
                defaultFormat.ForegroundColor = SuggestionForeground.Color;
            }

            return new SuggestionTokenFormat
            {
                Foreground = defaultFormat.ForegroundColor,
                Background = defaultFormat.BackgroundColor,
                Italic = defaultFormat.Italic,
                Bold = defaultFormat.Bold,
                Underline = defaultFormat.Underline
            };
        }

        private void ApplyDefaultFormatToRange(ITextRange range)
        {
            var defaultFormat = TextDocument.GetDefaultCharacterFormat();
            range.CharacterFormat.BackgroundColor = defaultFormat.BackgroundColor;
            range.CharacterFormat.ForegroundColor = defaultFormat.ForegroundColor;
            range.CharacterFormat.Bold = defaultFormat.Bold;
            range.CharacterFormat.Italic = defaultFormat.Italic;
            range.CharacterFormat.Underline = defaultFormat.Underline;
        }

        private void UpdateVisibleTokenList()
        {
            lock (LockObj)
            {
                var toBeRemoved = _visibleTokens.Where(x => !x.Active).ToArray();

                foreach (var elem in toBeRemoved)
                {
                    _visibleTokens.Remove(elem);
                }

                var toBeAdded = _tokens.Where(pair => pair.Value.Active && !_visibleTokens.Contains(pair.Value))
                    .Select(pair => pair.Value).ToArray();

                foreach (var elem in toBeAdded)
                {
                    _visibleTokens.Add(elem);
                }
            }
        }
    }
}
