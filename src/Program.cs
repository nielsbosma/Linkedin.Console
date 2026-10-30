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
});

return app.Run(args);
