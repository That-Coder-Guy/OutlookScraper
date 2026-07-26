namespace OutlookScraper.Core.Models;

/// <summary>
/// The closed set of campus event categories the model may choose from.
/// </summary>
/// <remarks>
/// This serves two purposes at once. It goes into the JSON schema as an <c>enum</c>,
/// which keeps the model from inventing categories; and it acts as stage 0 of the
/// blacklist cascade, where a category mismatch rules out a match outright.
///
/// It is not a second user-facing blacklist axis — the user still only ever
/// blacklists a topic tag. The category just stops a fraternity pizza rule from
/// ever being compared against a chemistry seminar.
/// </remarks>
public static class EventCategory
{
    public const string ClubMeeting = "club-meeting";
    public const string GreekLifeRecruitment = "greek-life-recruitment";
    public const string ReligiousGroup = "religious-group";
    public const string AcademicSeminar = "academic-seminar";
    public const string CareerFairOrInfoSession = "career-fair-or-info-session";
    public const string CulturalOrIdentityOrg = "cultural-or-identity-org";
    public const string SportsOrIntramural = "sports-or-intramural";
    public const string StudentGovernment = "student-government";
    public const string DepartmentSocial = "department-social";
    public const string ResearchStudyParticipation = "research-study-participation";
    public const string VolunteerOrService = "volunteer-or-service";
    public const string ArtsPerformance = "arts-performance";
    public const string HealthAndWellness = "health-and-wellness";
    public const string DormOrResidenceLife = "dorm-or-residence-life";
    public const string PoliticalOrAdvocacy = "political-or-advocacy";
    public const string VendorOrSponsorPromo = "vendor-or-sponsor-promo";
    public const string GeneralCampusEvent = "general-campus-event";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
    [
        ClubMeeting,
        GreekLifeRecruitment,
        ReligiousGroup,
        AcademicSeminar,
        CareerFairOrInfoSession,
        CulturalOrIdentityOrg,
        SportsOrIntramural,
        StudentGovernment,
        DepartmentSocial,
        ResearchStudyParticipation,
        VolunteerOrService,
        ArtsPerformance,
        HealthAndWellness,
        DormOrResidenceLife,
        PoliticalOrAdvocacy,
        VendorOrSponsorPromo,
        GeneralCampusEvent,
        Other,
    ];

    private static readonly HashSet<string> Lookup = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? category) =>
        !string.IsNullOrWhiteSpace(category) && Lookup.Contains(category);

    /// <summary>
    /// Coerces whatever the model produced onto the closed set. Grammar-constrained
    /// output should already be valid, but a malformed or truncated response must
    /// not be able to poison the blacklist's category gate.
    /// </summary>
    public static string Normalize(string? category) =>
        IsValid(category) ? category!.ToLowerInvariant() : Other;
}
