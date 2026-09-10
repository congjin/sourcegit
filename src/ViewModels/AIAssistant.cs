using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class AIAssistant : ObservableObject
    {
        /// <summary>
        ///     All configured AI platforms, so that the user can switch to another one
        ///     without closing this dialog (e.g. when the default one keeps failing).
        /// </summary>
        public List<AI.Service> Services
        {
            get;
        } = [];

        public AI.Service CurrentService
        {
            get => _service;
            set => SwitchService(value);
        }

        public List<string> AvailableModels
        {
            get => _service?.AvailableModels ?? [];
        }

        public string CurrentModel
        {
            get => _service?.Model;
            set
            {
                // Selection gets cleared (null) while the user is typing to filter
                // models; such intermediate states must not erase the saved model.
                if (value != null && _service != null)
                    _service.Model = value;
            }
        }

        public bool IsGenerating
        {
            get => _isGenerating;
            private set
            {
                if (SetProperty(ref _isGenerating, value))
                    OnPropertyChanged(nameof(ShowPlaceholder));
            }
        }

        /// <summary>
        ///     Raw streaming trace of the current generation (tool calls, token usage ...).
        /// </summary>
        public string Log
        {
            get => _log;
            private set => SetProperty(ref _log, value);
        }

        /// <summary>
        ///     Generated commit message. Editable, so that the user can polish it before
        ///     applying it to the working copy.
        /// </summary>
        public string Response
        {
            get => _response;
            set
            {
                if (SetProperty(ref _response, value))
                {
                    OnPropertyChanged(nameof(HasResult));
                    OnPropertyChanged(nameof(ShowPlaceholder));
                }
            }
        }

        public bool HasResult
        {
            get => !string.IsNullOrEmpty(_response);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                {
                    OnPropertyChanged(nameof(HasError));
                    OnPropertyChanged(nameof(ShowPlaceholder));
                }
            }
        }

        public bool HasError
        {
            get => !string.IsNullOrEmpty(_errorMessage);
        }

        public bool ShowPlaceholder
        {
            get => !_isGenerating && !HasResult && !HasError;
        }

        /// <summary>
        ///     The trace pane is only useful while something goes wrong or is still running.
        ///     It follows the generation state until the user expands/collapses it manually.
        /// </summary>
        public bool IsLogExpanded
        {
            get => _isLogExpanded;
            set
            {
                if (SetProperty(ref _isLogExpanded, value))
                    _logExpandedByUser = true;
            }
        }

        public AIAssistant(Repository repo, AI.Service service, List<Models.Change> changes)
        {
            _repo = repo;
            _service = service;
            _cancel = new CancellationTokenSource();

            foreach (var s in Preferences.Instance.OpenAIServices)
            {
                if (s != null && !Services.Contains(s))
                    Services.Add(s);
            }

            if (service != null && !Services.Contains(service))
                Services.Add(service);

            if (_service != null)
                _service.PropertyChanged += OnServicePropertyChanged;

            var builder = new StringBuilder();
            foreach (var c in changes)
                SerializeChange(c, builder);
            _changeList = builder.ToString();

            if (changes.Count > 0 && changes[0].DataForAmend is { } amend)
                _amendParent = amend.ParentSHA;
        }

        public async Task GenAsync()
        {
            if (_cancel is { IsCancellationRequested: false })
                _cancel.Cancel();
            _cancel = new CancellationTokenSource();

            // Every run owns an id so that a stale one (still unwinding after being
            // cancelled by a newer run) can never overwrite the fresher results.
            var runId = ++_runId;
            var service = _service;

            Log = string.Empty;
            ErrorMessage = null;
            IsGenerating = true;
            AutoExpandLog(true);

            // A platform that was never used before may have no model list yet. Pull it
            // first, otherwise the run below would fail just because nothing was picked.
            await EnsureModelsAsync(service);
            if (runId != _runId)
                return;

            EnsureDefaultModel(service);

            var agent = new AI.Agent(service);
            var builder = new StringBuilder();
            var responseBuilder = new StringBuilder();
            var foundResponse = false;
            var currentBranchName = _repo.CurrentBranch?.Name ?? "main";

            builder
                .Append("Platform: ").AppendLine(service?.Name ?? "-")
                .Append("Model: ").AppendLine(service?.Model ?? "-")
                .AppendLine()
                .AppendLine("Asking AI to generate commit message...").AppendLine();

            Log = builder.ToString();

            try
            {
                await agent.GenerateCommitMessageAsync(_repo.FullPath, currentBranchName, _changeList, _amendParent, message =>
                {
                    if (runId != _runId)
                        return;

                    builder.AppendLine(message);

                    if (foundResponse)
                    {
                        if (message.Equals("# Token Usage", StringComparison.Ordinal))
                            foundResponse = false;
                        else
                            responseBuilder.AppendLine(message);
                    }
                    else if (message.Equals("# Assistant", StringComparison.Ordinal))
                    {
                        foundResponse = true;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (runId == _runId)
                            Log = builder.ToString();
                    });
                }, _cancel.Token);

                if (runId != _runId)
                    return;

                var result = responseBuilder.ToString().Trim();
                if (string.IsNullOrEmpty(result) || result.Equals("[No content was generated.]", StringComparison.Ordinal))
                {
                    builder.AppendLine().AppendLine("[ERROR]").AppendLine(App.Text("AIAssistant.NoContent"));
                    Log = builder.ToString();
                    ErrorMessage = App.Text("AIAssistant.NoContent");
                    AutoExpandLog(true);
                }
                else
                {
                    Response = result;
                    AutoExpandLog(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Do nothing and leave `IsGenerating` to current (may already changed), so that the UI can update accordingly.
                return;
            }
            catch (Exception e)
            {
                if (runId != _runId)
                    return;

                builder
                    .AppendLine()
                    .AppendLine("[ERROR]")
                    .Append(e.Message);

                Log = builder.ToString();
                ErrorMessage = e.Message;
                AutoExpandLog(true);
            }

            IsGenerating = false;
        }

        public void Use(string text)
        {
            _repo.SetCommitMessage(text);
        }

        public void Use()
        {
            Use(_response);
        }

        public void Cancel()
        {
            // Invalidate the in-flight run before cancelling it, otherwise its late
            // callbacks would fight with whatever the user starts next.
            _runId++;
            _cancel?.Cancel();
            IsGenerating = false;
        }

        // Detaches the service subscription so the window-scoped VM can be collected.
        public void Release()
        {
            if (_service != null)
                _service.PropertyChanged -= OnServicePropertyChanged;
        }

        private void SwitchService(AI.Service service)
        {
            if (service == null || ReferenceEquals(service, _service))
                return;

            Release();
            _service = service;
            _service.PropertyChanged += OnServicePropertyChanged;

            OnPropertyChanged(nameof(CurrentService));
            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(CurrentModel));

            // Remember it so the next invocation for this repository starts on the
            // platform the user actually succeeded with.
            if (!Preferences.Instance.HasDefaultOpenAIService)
            {
                _repo.Settings.PreferredOpenAIService = service.Name;
                _repo.Settings.Save();
            }
        }

        private void EnsureDefaultModel(AI.Service service)
        {
            if (service == null)
                return;

            if (string.IsNullOrEmpty(service.Model) && service.AvailableModels is { Count: > 0 })
                service.Model = service.AvailableModels[0];
        }

        /// <summary>
        ///     A platform that has never been used may have no model list yet. Fetch it on
        ///     demand (off the UI thread) so that switching to it is immediately usable.
        /// </summary>
        private async Task EnsureModelsAsync(AI.Service service)
        {
            if (service == null || !service.AutoFetchAvailableModels)
                return;

            if (service.AvailableModels is { Count: > 0 })
                return;

            // Only ever try once per platform, so that a broken endpoint does not add
            // latency to every single re-generate.
            if (!_modelsRequested.Add(service))
                return;

            try
            {
                await Task.Run(() => service.FetchAvailableModels());
            }
            catch
            {
                // Ignore errors. The user can still type a model name manually.
            }
        }

        private void AutoExpandLog(bool expand)
        {
            // Once the user made a choice, stop second-guessing it.
            if (_logExpandedByUser || _isLogExpanded == expand)
                return;

            _isLogExpanded = expand;
            OnPropertyChanged(nameof(IsLogExpanded));
        }

        private void OnServicePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _service))
                return;

            // Forward the async model-list arrival and the fallback model pick so the
            // bound combo stays in sync with what the service will actually use.
            if (e.PropertyName == nameof(AI.Service.AvailableModels))
            {
                EnsureDefaultModel(_service);
                OnPropertyChanged(nameof(AvailableModels));
                OnPropertyChanged(nameof(CurrentModel));
            }
            else if (e.PropertyName == nameof(AI.Service.Model))
            {
                OnPropertyChanged(nameof(CurrentModel));
            }
        }

        private void SerializeChange(Models.Change c, StringBuilder builder)
        {
            var status = c.Index switch
            {
                Models.ChangeState.Added => "A",
                Models.ChangeState.Modified => "M",
                Models.ChangeState.Deleted => "D",
                Models.ChangeState.TypeChanged => "T",
                Models.ChangeState.Renamed => "R",
                Models.ChangeState.Copied => "C",
                _ => " ",
            };

            builder.Append(status).Append('\t');

            if (c.Index == Models.ChangeState.Renamed || c.Index == Models.ChangeState.Copied)
                builder.Append(c.OriginalPath).Append(" -> ").Append(c.Path).AppendLine();
            else
                builder.Append(c.Path).AppendLine();
        }

        private readonly Repository _repo = null;
        private readonly string _changeList = null;
        private readonly string _amendParent = null;
        private readonly HashSet<AI.Service> _modelsRequested = [];

        private AI.Service _service = null;
        private CancellationTokenSource _cancel = null;
        private int _runId = 0;
        private bool _isGenerating = false;
        private string _log = string.Empty;
        private string _response = string.Empty;
        private string _errorMessage = null;
        private bool _isLogExpanded = false;
        private bool _logExpandedByUser = false;
    }
}
