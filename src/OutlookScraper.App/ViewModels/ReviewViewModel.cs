using System.Collections.ObjectModel;
using System.Windows.Input;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Storage;

namespace OutlookScraper.App.ViewModels;

/// <summary>Backs the review window: pending suggestions plus a suppressed tab.</summary>
public sealed class ReviewViewModel : ObservableObject
{
    private readonly SuggestionRepository _suggestions;
    private readonly SuggestionActions _actions;

    private SuggestionViewModel? _selected;
    private string? _statusMessage;
    private bool _isBusy;

    public ReviewViewModel(SuggestionRepository suggestions, SuggestionActions actions)
    {
        _suggestions = suggestions;
        _actions = actions;

        AddCommand = new AsyncCommand(
            async p => await RunAsync(p, id => _actions.AddToCalendarAsync(id)),
            p => AsViewModel(p)?.CanAddToCalendar == true);

        BlacklistCommand = new AsyncCommand(
            async p => await RunAsync(p, id => _actions.BlacklistAsync(id)));

        DismissCommand = new AsyncCommand(
            async p => await RunAsync(p, id => _actions.DismissAsync(id)));

        RescueCommand = new AsyncCommand(
            async p => await RunAsync(p, id => _actions.RescueAsync(id)));

        RefreshCommand = new AsyncCommand(async _ => await LoadAsync());

        // Any action taken elsewhere — a toast button, most likely — should be
        // reflected here immediately.
        _actions.Changed += async (_, _) => await LoadAsync();
    }

    public ObservableCollection<SuggestionViewModel> Pending { get; } = [];

    public ObservableCollection<SuggestionViewModel> Suppressed { get; } = [];

    public ICommand AddCommand { get; }

    public ICommand BlacklistCommand { get; }

    public ICommand DismissCommand { get; }

    public ICommand RescueCommand { get; }

    public ICommand RefreshCommand { get; }

    public SuggestionViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasPending => Pending.Count > 0;

    public string EmptyMessage =>
        "Nothing waiting. New free-food events will appear here as they arrive.";

    public async Task LoadAsync()
    {
        var pending = await _suggestions.GetByStateAsync(SuggestionState.Pending);
        var suppressed = await _suggestions.GetByStateAsync(SuggestionState.Suppressed);

        // The window is created on the UI thread and this may be invoked from a
        // background action, so marshal before touching the bound collections.
        await OnUiThreadAsync(() =>
        {
            var previouslySelected = Selected?.Id;

            Pending.Clear();

            foreach (var suggestion in pending)
            {
                Pending.Add(new SuggestionViewModel(suggestion));
            }

            Suppressed.Clear();

            foreach (var suggestion in suppressed)
            {
                Suppressed.Add(new SuggestionViewModel(suggestion));
            }

            Selected = Pending.FirstOrDefault(s => s.Id == previouslySelected) ?? Pending.FirstOrDefault();

            OnPropertyChanged(nameof(HasPending));
        });
    }

    private async Task RunAsync(object? parameter, Func<Guid, Task<ActionResult>> action)
    {
        if (AsViewModel(parameter) is not { } target)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await action(target.Id);
            StatusMessage = result.Message;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    private SuggestionViewModel? AsViewModel(object? parameter) =>
        parameter as SuggestionViewModel ?? Selected;

    private static Task OnUiThreadAsync(Action work)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            work();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(work).Task;
    }
}
