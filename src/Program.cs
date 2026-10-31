using Linkedin.Console.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("linkedin");

    config.AddCommand<ProfileCommand>("profile")
        .WithDescription("Fetch a LinkedIn profile and output as YAML");

    config.AddCommand<PostCommand>("post")
        .WithDescription("Fetch LinkedIn posts and output as YAML");

    config.AddCommand<SetupCommand>("setup")
        .WithDescription("Print how to create your own LinkedIn app and token");

    config.AddBranch("auth", auth =>
    {
        auth.SetDescription("Manage the token for your own LinkedIn data");

        auth.AddCommand<AuthTokenCommand>("token")
            .WithDescription("Check a token from the portal and store it");

        auth.AddCommand<AuthStatusCommand>("status")
            .WithDescription("Show the token's source and expiry, and whether change logging is on");

        auth.AddCommand<AuthLogoutCommand>("logout")
            .WithDescription("Remove the token stored on this computer");
    });

    config.AddBranch("me", me =>
    {
        me.SetDescription("Read your own LinkedIn data through LinkedIn's portability API");

        me.AddCommand<MeDomainsCommand>("domains")
            .WithDescription("List the data domains");

        me.AddCommand<MeSnapshotCommand>("snapshot")
            .WithDescription("Fetch domains to stdout, or everything to a folder");

        me.AddCommand<MeChangelogCommand>("changelog")
            .WithDescription("Read recent activity, up to 28 days back");

        me.AddCommand<MeSyncCommand>("sync")
            .WithDescription("Append new activity to a file, so history outlasts the 28 days");
    });

    config.AddBranch("outreach", outreach =>
    {
        outreach.SetDescription("Work through a list of people: the tool prepares each invitation, you press Send on LinkedIn");

        outreach.AddCommand<OutreachAddCommand>("add")
            .WithDescription("Add people from a CSV file");

        outreach.AddCommand<OutreachNextCommand>("next")
            .WithDescription("Copy the next note, open the profile, and record what you did");

        outreach.AddCommand<OutreachListCommand>("list")
            .WithDescription("Show the list and how much of the caps is used");

        outreach.AddCommand<OutreachNeverCommand>("never")
            .WithDescription("Mark a person as do-not-contact, for good");

        outreach.AddCommand<OutreachPauseCommand>("pause")
            .WithDescription("Stop outreach next until a date");
    });
});

return app.Run(args);
