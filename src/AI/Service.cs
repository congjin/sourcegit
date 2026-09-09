using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using Avalonia.Threading;

using Azure.AI.OpenAI;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenAI;
using OpenAI.Chat;

namespace SourceGit.AI
{
    public class Service : ObservableObject
    {
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Server
        {
            get;
            set;
        } = string.Empty;

        public string ApiKey
        {
            get;
            set;
        } = string.Empty;

        public bool ReadApiKeyFromEnv
        {
            get;
            set;
        } = false;

        [JsonIgnore]
        public List<string> AvailableModels
        {
            get;
            private set;
        } = [];

        public string Model
        {
            get => _model;
            set
            {
                // The value may transiently become null while the user is typing to
                // filter models in the selector; such states must not erase the model.
                if (value != null)
                    SetProperty(ref _model, value);
            }
        }

        public bool AutoFetchAvailableModels
        {
            get => _autoFetchAvailableModels;
            set => SetProperty(ref _autoFetchAvailableModels, value);
        }

        public string ReasoningEffortLevel
        {
            get => _reasoningEffortLevel;
            set => SetProperty(ref _reasoningEffortLevel, value);
        }

        public string AdditionalPrompt
        {
            get;
            set;
        } = string.Empty;

        public string ExtraHeaders
        {
            get;
            set;
        } = string.Empty;

        public void FetchAvailableModels()
        {
            if (!_autoFetchAvailableModels)
            {
                if (!string.IsNullOrEmpty(Model))
                {
                    AvailableModels = [Model];
                    NotifyAvailableModelsChanged();
                }

                return;
            }

            var allModels = GetOpenAIClient().GetOpenAIModelClient().GetModels();
            var models = new List<string>();
            foreach (var model in allModels.Value)
                models.Add(model.Id);

            // Respect the model explicitly set by the user (the default one or the last
            // used); only pick the first available when nothing has been chosen yet.
            var fallback = models.Count > 0 && string.IsNullOrEmpty(Model) ? models[0] : null;
            AvailableModels = models;

            Dispatcher.UIThread.Post(() =>
            {
                // The user may have picked a model while the fetch was in flight;
                // only fill in the fallback when nothing has been chosen yet.
                if (fallback != null && string.IsNullOrEmpty(Model))
                    Model = fallback;

                OnPropertyChanged(nameof(AvailableModels));
            });
        }

        private void NotifyAvailableModelsChanged()
        {
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(AvailableModels)));
        }

        public ChatClient GetChatClient()
        {
            return !string.IsNullOrEmpty(Model) ? GetOpenAIClient().GetChatClient(Model) : null;
        }

        private OpenAIClient GetOpenAIClient()
        {
            var credential = new ApiKeyCredential(ReadApiKeyFromEnv ? Environment.GetEnvironmentVariable(ApiKey) : ApiKey);

            if (Server.Contains("openai.azure.com/", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(ExtraHeaders))
                    return new AzureOpenAIClient(new Uri(Server), credential);

                var azureOptions = new AzureOpenAIClientOptions();
                azureOptions.AddPolicy(new ExtraHeadersPolicy(ExtraHeaders), PipelinePosition.PerCall);
                return new AzureOpenAIClient(new Uri(Server), credential, azureOptions);
            }

            var options = new OpenAIClientOptions() { Endpoint = new Uri(Server) };
            if (!string.IsNullOrEmpty(ExtraHeaders))
                options.AddPolicy(new ExtraHeadersPolicy(ExtraHeaders), PipelinePosition.PerCall);
            return new OpenAIClient(credential, options);
        }

        private string _name = string.Empty;
        private string _model = string.Empty;
        private string _reasoningEffortLevel = Options.IgnoredReasoningEffortLevel;
        private bool _autoFetchAvailableModels = true;
    }
}
