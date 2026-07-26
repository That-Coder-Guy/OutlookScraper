using OutlookScraper.Core.Models;
using OutlookScraper.Core.Time;

namespace OutlookScraper.App.ViewModels;

/// <summary>One suggestion row in the review window.</summary>
public sealed class SuggestionViewModel(EventSuggestion suggestion) : ObservableObject
{
    private string? _status;

    public EventSuggestion Model { get; } = suggestion;

    public Guid Id => Model.Id;

    public string Title => string.IsNullOrWhiteSpace(Model.Title)
        ? "Free food on campus"
        : Model.Title;

    public string Organization => Model.Organization;

    public string Food => Model.FoodDescription;

    public string Location => string.IsNullOrWhiteSpace(Model.Location)
        ? "Location not stated"
        : Model.Location;

    public string Sender => string.IsNullOrWhiteSpace(Model.SenderName)
        ? Model.SenderAddress
        : $"{Model.SenderName} <{Model.SenderAddress}>";

    public string Subject => Model.Subject;

    public string BodyExcerpt => Model.BodyExcerpt;

    public string TopicTag => Model.TopicTag;

    public string Category => Model.Category;

    public string Reason => Model.Reason;

    public string When
    {
        get
        {
            if (Model.StartUtc is not { } start)
            {
                return "No date stated — open the email to check";
            }

            var zone = TimeZoneResolution.Resolve(Model.IanaTimeZone);
            var local = TimeZoneInfo.ConvertTime(start, zone);

            return Model.IsAllDay
                ? local.ToString("dddd d MMMM") + " (all day)"
                : local.ToString("dddd d MMMM, h:mm tt");
        }
    }

    /// <summary>
    /// Shown when the model admitted to inferring the date, or could not find one. A
    /// guessed date going straight onto a real calendar is worth a glance first.
    /// </summary>
    public bool NeedsDateReview => Model.NeedsDateReview;

    public string? DateWarning => Model.NeedsDateReview
        ? Model.StartUtc is null
            ? "No date could be extracted from this email."
            : "The date was inferred rather than stated — please check it."
        : null;

    public bool CanAddToCalendar => Model.CanAddToCalendar;

    /// <summary>Why this was hidden, for the Suppressed tab.</summary>
    public string? SuppressionDetail => Model.State == SuggestionState.Suppressed
        ? $"Hidden by a blacklist rule ({FormatStage(Model.SuppressStage)}" +
          (Model.SuppressScore is { } score ? $", score {score:F2})" : ")")
        : null;

    public bool IsSoftSuppressed => Model.IsSoftSuppressed;

    /// <summary>Transient feedback after an action, e.g. "Added to calendar".</summary>
    public string? Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private static string FormatStage(SuppressStage stage) => stage switch
    {
        SuppressStage.Exact => "identical topic",
        SuppressStage.Tokens => "very similar topic",
        SuppressStage.SemanticStrong => "similar meaning",
        SuppressStage.SemanticSoft => "possibly similar meaning",
        _ => "unknown",
    };
}
