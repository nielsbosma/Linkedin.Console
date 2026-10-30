namespace Linkedin.Console.Snapshots;

public sealed record DomainInfo(string Name, string Description, bool Supported = true, bool Documented = true);

/// <summary>
/// LinkedIn's documented domains, checked against the API on 2026-10-30. The docs do not match the
/// API: they spell <c>Events</c> and <c>login</c> in uppercase, which the API refuses, and leave out
/// <c>WHATSAPP_NUMBERS</c>. So this is only used for describing and spelling domains: any name can
/// be requested, and a full export follows what LinkedIn itself lists.
/// </summary>
public static class DomainCatalog
{
    public static readonly IReadOnlyList<DomainInfo> All =
    [
        new("ADS_CLICKED", "Ads you clicked"),
        new("MEMBER_FOLLOWING", "People you follow"),
        new("login", "Stored login records"),
        new("RICH_MEDIA", "Photos, videos and documents you shared"),
        new("SEARCHES", "Your recent LinkedIn searches"),
        new("INFERENCE_TAKEOUT", "Inferences about you from profile and activity"),
        new("ALL_COMMENTS", "Your comments, except on Group posts"),
        new("CONTACTS", "Contacts imported into LinkedIn"),
        new("Events", "Events attended, declined or requested"),
        new("RECEIPTS", "Purchases tied to the account"),
        new("AD_TARGETING", "Data used to choose ads for you"),
        new("REGISTRATION", "The date you joined"),
        new("REVIEWS", "Ratings and reviews you wrote"),
        new("ARTICLES", "Articles you wrote"),
        new("PATENTS", "Patents on your profile"),
        new("GROUPS", "Groups you belong to"),
        new("COMPANY_FOLLOWS", "Companies and other entities you follow"),
        new("INVITATIONS", "Invitations sent and received"),
        new("PHONE_NUMBERS", "Phone numbers on the account"),
        new("CONNECTIONS", "First-degree connections"),
        new("EMAIL_ADDRESSES", "Current and past email addresses"),
        new("JOB_POSTINGS", "Jobs you posted"),
        new("JOB_APPLICATIONS", "Jobs you applied to"),
        new("JOB_SEEKER_PREFERENCES", "Job type, location and industry preferences"),
        new("LEARNING", "LinkedIn Learning videos watched"),
        new("INBOX", "Messages sent and received"),
        new("SAVED_JOBS", "Jobs saved for later"),
        new("SAVED_JOB_ALERTS", "Job alerts and when they were saved"),
        new("PROFILE", "Basic profile information"),
        new("SKILLS", "Skills on your profile"),
        new("POSITIONS", "Job roles and dates on your profile"),
        new("EDUCATION", "Schools, degrees and activities on your profile"),
        new("TEST_SCORES", "Test scores on your profile"),
        new("CAUSES_YOU_CARE_ABOUT", "Causes on your profile"),
        new("PUBLICATIONS", "Publications on your profile"),
        new("PROJECTS", "Projects on your profile"),
        new("ORGANIZATIONS", "Organizations on your profile"),
        new("LANGUAGES", "Languages and proficiency levels"),
        new("HONORS", "Honors on your profile"),
        new("COURSES", "Courses on your profile"),
        new("CERTIFICATIONS", "Certifications on your profile"),
        new("RECOMMENDATIONS", "Recommendations given and received"),
        new("ENDORSEMENTS", "Endorsements given and received"),
        new("MEMBER_SHARE_INFO", "Posts you shared and reshared"),
        new("SECURITY_CHALLENGE_PIPE", "Sign-in challenges and device details"),
        new("TRUSTED_GRAPH", "Identity, workplace and education verifications"),
        new("MARKETPLACE_ENGAGEMENTS", "Services Marketplace events and status"),
        new("MARKETPLACE_PROVIDERS", "Services you offer"),
        new("MARKETPLACE_OPPORTUNITIES", "Services you are looking for"),
        new("ACTOR_SAVE_ITEM", "Saved posts, articles and other content"),
        new("JOB_APPLICANT_SAVED_ANSWERS", "Saved answers to basic application questions"),
        new("TALENT_QUESTION_SAVED_RESPONSE", "Answers to a job poster's questions"),
        new("PROFILE_SUMMARY", "AI-generated profile summary"),
        new("ALL_LIKES", "Your reactions to posts"),
        new("ALL_VOTES", "Polls created and voted on"),
        new("RECEIPTS_LBP", "Purchases of LinkedIn services"),
        new("EASYAPPLY_BLOCKING", "Records for third-party application tracking", Supported: false),
        new("LEARNING_COACH_AI_TAKEOUT", "Conversations with the Learning Coach chatbot"),
        new("LEARNING_COACH_INBOX", "Conversations with the Learning Coach chatbot"),
        new("LEARNING_ROLEPLAY_INBOX", "Feedback from learning practice scenarios"),
        new("VOLUNTEERING_EXPERIENCES", "Volunteer roles, causes and dates"),
        new("ACCOUNT_HISTORY", "Account status changes in the past year"),
        new("INSTANT_REPOSTS", "Dates, times and links of your reposts"),
        new("IDENTITY_CREDENTIALS_AND_ASSETS", "Private identity assets and credentials"),
        new("ADS_LAN", "Ad engagement records"),
        new("PREMIUM_NOTES", "Private notes on connections"),
        new("WHATSAPP_NUMBERS", "WhatsApp numbers on the account", Documented: false)
    ];

    public static DomainInfo? Find(string name) => All.FirstOrDefault(d => d.Name == name);

    /// <summary>LinkedIn's names are case-sensitive and mostly uppercase, so users need not care.</summary>
    public static string Spell(string name) =>
        All.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name ?? name.ToUpperInvariant();

    /// <summary>Domains LinkedIn could return but did not, which is where "not ready yet" hides.</summary>
    public static List<string> NotReturned(IEnumerable<string> returned)
    {
        var seen = returned.ToHashSet();
        return All.Where(d => d.Supported && !seen.Contains(d.Name)).Select(d => d.Name).ToList();
    }
}
