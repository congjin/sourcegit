using System;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;

using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;

namespace SourceGit.Views
{
    public class AIResponseView : TextEditor
    {
        public class LineStyleTransformer : DocumentColorizingTransformer
        {
            protected override void ColorizeLine(DocumentLine line)
            {
                var content = CurrentContext.Document.GetText(line);
                if (content.StartsWith("Read changes in file: ", StringComparison.Ordinal))
                {
                    ChangeLinePart(line.Offset + 22, line.EndOffset, v =>
                    {
                        v.TextRunProperties.SetForegroundBrush(Brushes.DeepSkyBlue);
                        v.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    });
                }
            }
        }

        public static readonly DirectProperty<AIResponseView, string> ContentProperty =
            AvaloniaProperty.RegisterDirect<AIResponseView, string>(
                nameof(Content),
                static o => o.Content,
                static (o, v) => o.Content = v);

        /// <summary>
        ///     Text of this editor. Two-way: programmatic updates push into the document and
        ///     user edits are published back, so that the result can be edited in place.
        /// </summary>
        public string Content
        {
            get => _content;
            set => SetAndRaise(ContentProperty, ref _content, value);
        }

        protected override Type StyleKeyOverride => typeof(TextEditor);

        public AIResponseView() : base(new TextArea(), new TextDocument())
        {
            IsReadOnly = true;
            ShowLineNumbers = false;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            TextArea.TextView.Margin = new Thickness(4, 0);
            TextArea.TextView.Options.EnableHyperlinks = false;
            TextArea.TextView.Options.EnableEmailHyperlinks = false;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            TextArea.TextView.ContextRequested += OnTextViewContextRequested;

            if (!_documentHooked)
            {
                Document.TextChanged += OnDocumentTextChanged;
                _documentHooked = true;
            }

            if (_textMate == null)
            {
                _textMate = Models.TextMateHelper.CreateForEditor(this);
                Models.TextMateHelper.SetGrammarByFileName(_textMate, "README.md");
                TextArea.TextView.LineTransformers.Add(new LineStyleTransformer());
            }
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);

            TextArea.TextView.ContextRequested -= OnTextViewContextRequested;

            if (_documentHooked)
            {
                Document.TextChanged -= OnDocumentTextChanged;
                _documentHooked = false;
            }

            if (_textMate != null)
            {
                _textMate.Dispose();
                _textMate = null;
            }

            GC.Collect();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ContentProperty)
            {
                if (string.Equals(Text, _content, StringComparison.Ordinal))
                    return;

                _applyingContent = true;
                Text = _content ?? string.Empty;
                _applyingContent = false;
            }
            else if (change.Property.Name == nameof(ActualThemeVariant) && change.NewValue != null)
            {
                Models.TextMateHelper.SetThemeByApp(_textMate);
            }
        }

        private void OnDocumentTextChanged(object sender, EventArgs e)
        {
            // Publishing back while a programmatic update is being applied would recurse
            // into the very setter that started it.
            if (_applyingContent)
                return;

            SetAndRaise(ContentProperty, ref _content, Document.Text);
        }

        private void OnTextViewContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (DataContext is not ViewModels.AIAssistant vm)
                return;

            var menu = new ContextMenu();
            var selected = SelectedText;
            var hasSelection = !string.IsNullOrEmpty(selected);

            if (hasSelection)
            {
                var apply = new MenuItem() { Header = App.Text("AIAssistant.Use") };
                apply.Icon = this.CreateMenuIcon("Icons.Check");
                apply.Click += (_, ev) =>
                {
                    vm.Use(selected);
                    ev.Handled = true;
                };
                menu.Items.Add(apply);

                var copy = new MenuItem() { Header = App.Text("Copy") };
                copy.Icon = this.CreateMenuIcon("Icons.Copy");
                copy.Click += async (_, ev) =>
                {
                    await this.CopyTextAsync(selected);
                    ev.Handled = true;
                };
                menu.Items.Add(copy);
            }

            if (!IsReadOnly)
            {
                if (menu.Items.Count > 0)
                    menu.Items.Add(new MenuItem() { Header = "-" });

                if (hasSelection)
                {
                    var cut = new MenuItem() { Header = App.Text("Cut") };
                    cut.Icon = this.CreateMenuIcon("Icons.Cut");
                    cut.Click += async (_, ev) =>
                    {
                        await this.CopyTextAsync(selected);
                        Document.Replace(SelectionStart, SelectionLength, string.Empty);
                        ev.Handled = true;
                    };
                    menu.Items.Add(cut);
                }

                var paste = new MenuItem() { Header = App.Text("Paste") };
                paste.Icon = this.CreateMenuIcon("Icons.Paste");
                paste.Click += async (_, ev) =>
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null)
                    {
                        var text = await clipboard.TryGetTextAsync();
                        if (!string.IsNullOrEmpty(text))
                            Document.Replace(SelectionStart, SelectionLength, text);
                    }

                    ev.Handled = true;
                };
                menu.Items.Add(paste);
            }

            if (menu.Items.Count == 0)
                return;

            menu.Open(TextArea.TextView);
            e.Handled = true;
        }

        private TextMate.Installation _textMate = null;
        private string _content = string.Empty;
        private bool _documentHooked = false;
        private bool _applyingContent = false;
    }

    public partial class AIAssistant : ChromelessWindow
    {
        public AIAssistant()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // Combo boxes are already populated by now, so anything raised from here on is
            // a real user pick. Declared before the first run so that switching platform
            // while it is still generating stays responsive.
            _ready = true;

            if (DataContext is ViewModels.AIAssistant vm)
                await GenerateAsync(vm);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // Popup teardown during window close can fire DropDownClosed -> a model commit;
            // drop those so GenAsync is never restarted on a closing window.
            _closing = true;

            base.OnClosing(e);
            (DataContext as ViewModels.AIAssistant)?.Cancel();
            (DataContext as ViewModels.AIAssistant)?.Release();

            // Persist the last used model/platform so that it becomes the default on next launch.
            ViewModels.Preferences.Instance.Save();
        }

        private async void OnServiceChanged(object sender, SelectionChangedEventArgs e)
        {
            // Switching the platform re-runs generation on the newly selected one, which is
            // the main escape hatch when the default platform keeps failing.
            if (_closing || !_ready)
                return;

            if (DataContext is ViewModels.AIAssistant vm && IsLoaded)
                await GenerateAsync(vm);

            e.Handled = true;
        }

        private async void OnModelCommitted(object sender, EventArgs e)
        {
            // Switching the model re-runs generation with the newly committed model.
            if (_closing || !_ready)
                return;

            if (DataContext is ViewModels.AIAssistant vm && IsLoaded)
                await GenerateAsync(vm);
        }

        private void OnStopClicked(object sender, RoutedEventArgs e)
        {
            (DataContext as ViewModels.AIAssistant)?.Cancel();
            e.Handled = true;
        }

        private async void OnCopyClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.AIAssistant vm && !string.IsNullOrEmpty(vm.Response))
                await this.CopyTextAsync(vm.Response);

            e.Handled = true;
        }

        private void OnUseClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.AIAssistant vm && vm.HasResult)
                vm.Use();

            Close();
            e.Handled = true;
        }

        private async void OnRegenClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.AIAssistant vm)
                await GenerateAsync(vm);

            e.Handled = true;
        }

        /// <summary>
        ///     Runs a generation and hands the caret to the result editor afterwards, so the
        ///     message can be refined without reaching for the mouse. Skipped when the window
        ///     is inactive: this is a non-modal dialog and stealing focus is not acceptable.
        /// </summary>
        private async Task GenerateAsync(ViewModels.AIAssistant vm)
        {
            await vm.GenAsync();

            if (vm.HasResult && IsActive)
                ResultEditor.Focus();
        }

        private bool _closing = false;
        private bool _ready = false;
    }
}
