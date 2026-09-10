using System;
using System.Collections;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    /// <summary>
    ///     An editable ComboBox that filters the dropdown items by a keyword as the user types.
    ///     The committed value is exposed via <see cref="SelectedModel"/> and is only updated on
    ///     explicit user actions (picking an item or pressing ENTER), never while typing.
    /// </summary>
    public class FilterableComboBox : ComboBox
    {
        protected override Type StyleKeyOverride => typeof(ComboBox);

        public static readonly DirectProperty<FilterableComboBox, string> SelectedModelProperty =
            AvaloniaProperty.RegisterDirect<FilterableComboBox, string>(
                nameof(SelectedModel),
                static o => o.SelectedModel,
                static (o, v) => o.SelectedModel = v);

        /// <summary>
        ///     Raised after the user commits a new value.
        /// </summary>
        public event EventHandler SelectionCommitted;

        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (SetAndRaise(SelectedModelProperty, ref _selectedModel, value))
                    SyncDisplay(value);
            }
        }

        public FilterableComboBox()
        {
            IsEditable = true;

            DropDownOpened += OnSelfDropDownOpened;
            DropDownClosed += OnSelfDropDownClosed;
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_inputTextBox != null)
            {
                _inputTextBox.RemoveHandler(KeyDownEvent, OnTextBoxPreviewKeyDown);
                _inputTextBox.TextChanged -= OnTextBoxTextChanged;
                _inputTextBox.LostFocus -= OnTextBoxLostFocus;
            }

            _inputTextBox = e.NameScope.Find<TextBox>("PART_EditableTextBox");
            if (_inputTextBox != null)
            {
                _inputTextBox.AddHandler(KeyDownEvent, OnTextBoxPreviewKeyDown, RoutingStrategies.Tunnel);
                _inputTextBox.TextChanged += OnTextBoxTextChanged;
                _inputTextBox.LostFocus += OnTextBoxLostFocus;
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ItemsSourceProperty && !_filtering)
            {
                _fullItems = change.NewValue as IEnumerable;
                ApplyFilter(_filterText);

                // The list may be swapped asynchronously (e.g. another platform was picked
                // and its models arrived late). Re-apply the current value so the box keeps
                // showing it. Skipped while filtering, which would clobber the keyword.
                if (!IsDropDownOpen)
                    SyncDisplay(_selectedModel);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // While the dropdown is open and focus is in the text box, typed keys are text
            // entry. Without this, Space would bubble into the base's "select focused item
            // and close" path and collapse the list mid-filtering.
            if (IsDropDownOpen && e.Key == Key.Space && _inputTextBox is { IsKeyboardFocusWithin: true })
                return;

            base.OnKeyDown(e);
        }

        private void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsDropDownOpen)
            {
                if (e.Key == Key.Down)
                {
                    MoveSelection(1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Up)
                {
                    MoveSelection(-1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    _enterPressed = true;
                    CommitPending();
                    IsDropDownOpen = false;
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    RestoreDisplay();
                    IsDropDownOpen = false;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Enter)
            {
                _enterPressed = true;
                CommitPending();
                e.Handled = true;
            }
        }

        private void OnTextBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingText || _filtering || _inputTextBox == null)
                return;

            // Only react to text typed by the user; programmatic updates always match the
            // text of the currently selected item.
            var text = _inputTextBox.Text ?? string.Empty;
            if (IsDropDownOpen)
            {
                ApplyFilter(text);

                // The base control exact-matches the typed text and may move keyboard focus
                // to a realized item container when it hits; reclaim it so typing continues.
                if (_inputTextBox is { IsKeyboardFocusWithin: false })
                {
                    _inputTextBox.Focus();
                    _inputTextBox.CaretIndex = _inputTextBox.Text?.Length ?? 0;
                }
            }
            else if (text.Length > 0 && ItemCount > 0 && !text.Equals(GetItemText(SelectedItem), StringComparison.Ordinal))
            {
                _openedByTyping = true;
                IsDropDownOpen = true;
            }
        }

        private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
        {
            // While the dropdown is open focus legitimately hops between the text box and
            // item containers; committing here would roll back the filter mid-typing.
            // The commit happens in OnSelfDropDownClosed instead.
            if (IsDropDownOpen)
                return;

            CommitPending();
        }

        private void OnSelfDropDownOpened(object sender, EventArgs e)
        {
            var keyword = _openedByTyping ? (_inputTextBox?.Text ?? string.Empty) : string.Empty;
            ApplyFilter(keyword);

            // Base control moves focus to the selected item on open; bring it back to the
            // text box so that the user can type to filter immediately.
            if (_inputTextBox != null)
            {
                _inputTextBox.Focus();
                if (_openedByTyping)
                    _inputTextBox.CaretIndex = _inputTextBox.Text?.Length ?? 0;
                else
                    _inputTextBox.SelectAll();
            }

            _openedByTyping = false;
        }

        private void OnSelfDropDownClosed(object sender, EventArgs e)
        {
            CommitPending();
            ApplyFilter(string.Empty);
            RestoreDisplay();
        }

        private void ApplyFilter(string keyword)
        {
            var source = _fullItems;
            if (source == null)
                return;

            List<object> filtered = null;
            if (!string.IsNullOrEmpty(keyword))
            {
                filtered = new List<object>();
                foreach (var item in source)
                {
                    if (GetItemText(item).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        filtered.Add(item);
                }
            }

            _filtering = true;
            SetCurrentValue(ItemsSourceProperty, filtered ?? source);
            _filtering = false;
            _filterText = keyword;
        }

        private void MoveSelection(int delta)
        {
            var count = ItemCount;
            if (count <= 0)
                return;

            var index = SelectedIndex;
            if (index < 0)
                index = delta > 0 ? -1 : 0;

            index = (index + delta) % count;
            if (index < 0)
                index += count;

            // Base pushes the selected item's text into the box on selection change;
            // suppress our handler so the keyword-filtered list is kept intact.
            _syncingText = true;
            SelectedIndex = index;
            _syncingText = false;

            // Keep the focus in the text box so typing continues to filter the list.
            if (_inputTextBox != null && !_inputTextBox.IsFocused)
            {
                _inputTextBox.Focus();
                _inputTextBox.CaretIndex = _inputTextBox.Text?.Length ?? 0;
            }
        }

        private void CommitPending()
        {
            var entered = _enterPressed;
            _enterPressed = false;

            // The item list may be swapped out asynchronously (e.g. model list arriving
            // late), which resets SelectedItem; resolve the current text against whatever
            // list is live at commit time so a legit pick is never downgraded to raw text.
            var text = _inputTextBox?.Text;
            if (!string.IsNullOrEmpty(text) && TryFindItem(text, out var matched))
            {
                if (!string.Equals(GetItemText(matched), _selectedModel, StringComparison.Ordinal))
                    SetSelectedModel(GetItemText(matched));
                else
                    RestoreDisplay();
                return;
            }

            if (SelectedItem is string selected)
            {
                if (!string.Equals(selected, _selectedModel, StringComparison.Ordinal))
                    SetSelectedModel(selected);
                else
                    RestoreDisplay();
                return;
            }

            // Only a text that exactly matches an item can be committed outside explicit
            // actions; but a text that matches NO item at all can only be an intentional
            // custom id (e.g. a private deployment), so it commits on blur as well.
            // Partial filter keywords are always rolled back.
            var typed = text?.Trim();
            if (!string.IsNullOrEmpty(typed) &&
                !string.Equals(typed, _selectedModel, StringComparison.Ordinal) &&
                (entered || HasNoMatch(typed)))
            {
                SetSelectedModel(typed);
                return;
            }

            RestoreDisplay();
        }

        private bool HasNoMatch(string keyword)
        {
            var source = _fullItems ?? ItemsSource as IEnumerable;
            if (source == null)
                return true;

            foreach (var item in source)
            {
                if (GetItemText(item).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private bool TryFindItem(string text, out object matched)
        {
            matched = null;
            if (ItemsSource is IEnumerable source)
            {
                foreach (var item in source)
                {
                    if (string.Equals(GetItemText(item), text, StringComparison.Ordinal))
                    {
                        matched = item;
                        return true;
                    }
                }
            }

            return false;
        }

        private void SetSelectedModel(string value)
        {
            var old = _selectedModel;
            _selectedModel = value;
            SyncDisplay(value);
            RaisePropertyChanged(SelectedModelProperty, old, value);
            SelectionCommitted?.Invoke(this, EventArgs.Empty);
        }

        private void RestoreDisplay()
        {
            SyncDisplay(_selectedModel);
        }

        private void SyncDisplay(string value)
        {
            object match = null;
            if (value != null && ItemsSource is IEnumerable source)
            {
                foreach (var item in source)
                {
                    if (string.Equals(GetItemText(item), value, StringComparison.Ordinal))
                    {
                        match = item;
                        break;
                    }
                }
            }

            if (match != null)
            {
                if (!Equals(SelectedItem, match))
                    SelectedItem = match;
            }
            else if (SelectedItem != null)
            {
                SelectedItem = null;
            }

            var text = value ?? string.Empty;
            if (!string.Equals(Text, text, StringComparison.Ordinal))
            {
                _syncingText = true;
                SetCurrentValue(TextProperty, text);
                _syncingText = false;
            }
        }

        private static string GetItemText(object item)
        {
            if (item == null)
                return string.Empty;
            return item as string ?? item.ToString();
        }

        private TextBox _inputTextBox = null;
        private IEnumerable _fullItems = null;
        private string _selectedModel = string.Empty;
        private string _filterText = string.Empty;
        private bool _filtering = false;
        private bool _syncingText = false;
        private bool _openedByTyping = false;
        private bool _enterPressed = false;
    }
}
