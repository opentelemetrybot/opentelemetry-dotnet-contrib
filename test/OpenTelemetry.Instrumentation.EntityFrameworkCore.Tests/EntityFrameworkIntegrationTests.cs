// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Instrumentation.SqlClient.Tests;
using OpenTelemetry.Tests;
using OpenTelemetry.Trace;
using Testcontainers.MsSql;
using Testcontainers.SqlEdge;
using Xunit;

namespace OpenTelemetry.Instrumentation.EntityFrameworkCore.Tests;

[Trait("CategoryName", "SqlIntegrationTests")]
public sealed class EntityFrameworkIntegrationTests : IClassFixture<SqlClientIntegrationTestsFixture>
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";

    private const string ActivitySourceName = "OpenTelemetry.Instrumentation.EntityFrameworkCore";

    private readonly SqlClientIntegrationTestsFixture fixture;

    public EntityFrameworkIntegrationTests(SqlClientIntegrationTestsFixture fixture)
    {
        this.fixture = fixture;
    }

    public static TheoryData<string, string, bool, bool, Type, string, string> RawSqlTestCases()
    {
        (string, Type, string, string)[] providers =
        [
            (SqliteProvider, typeof(SqliteCommand), "sqlite", "main"),
            (SqlServerProvider, typeof(SqlCommand), "mssql", "master"),
        ];

        var testCases = new TheoryData<string, string, bool, bool, Type, string, string>();

        foreach ((var provider, var commandType, var system, var database) in providers)
        {
            testCases.Add(provider, "select 1/1", false, false, commandType, system, database);
            testCases.Add(provider, "select 1/1", true, false, commandType, system, database);

            // For some reason, SQLite does not throw an exception for division by zero
            if (provider != SqliteProvider)
            {
                testCases.Add(provider, "select 1/0", false, true, commandType, system, database);
                testCases.Add(provider, "select 1/0", true, true, commandType, system, database);
            }
        }

        return testCases;
    }

    public static TheoryData<string, bool, bool, Type, string, string> DataContextTestCases()
    {
        (string, Type, string, string)[] providers =
        [
            (SqliteProvider, typeof(SqliteCommand), "sqlite", "main"),
            (SqlServerProvider, typeof(SqlCommand), "mssql", "master"),
        ];

        bool[] trueFalse = [true, false];

        var testCases = new TheoryData<string, bool, bool, Type, string, string>();

        foreach ((var provider, var commandType, var system, var database) in providers)
        {
            foreach (var captureTextCommandContent in trueFalse)
            {
                foreach (var shouldEnrich in trueFalse)
                {
                    testCases.Add(provider, captureTextCommandContent, shouldEnrich, commandType, system, database);
                }
            }
        }

        return testCases;
    }

    [EnabledOnDockerPlatformTheory(EnabledOnDockerPlatformTheoryAttribute.DockerPlatform.Linux)]
    [MemberData(nameof(RawSqlTestCases))]
    public async Task TracesRawSql(
        string provider,
        string commandText,
        bool captureTextCommandContent,
        bool isFailure,
        Type expectedCommandType,
        string expectedSystemName,
        string expectedDatabaseName)
    {
        // In the case of SQL Server, the activity we're interested in is the one
        // created by the SqlClient instrumentation which is a child of EFCore.
        var expectedSourceName = isFailure && provider is SqlServerProvider
            ? "OpenTelemetry.Instrumentation.SqlClient"
            : ActivitySourceName;

        var filtered = false;

        bool ActivityFilter(string? providerName, IDbCommand command)
        {
            filtered = true;

            Assert.True(providerName == provider || providerName == null, $"The provider name {providerName} is not null or the expected value.");
            Assert.IsType(expectedCommandType, command, false);

            return true;
        }

        var activities = new List<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddInMemoryExporter(activities)
            .AddSqlClientInstrumentation(options =>
            {
                options.SetDbStatementForText = captureTextCommandContent;
            })
            .AddEntityFrameworkCoreInstrumentation(options =>
            {
                options.Filter = ActivityFilter;
                options.SetDbStatementForText = captureTextCommandContent;
            })
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<ItemsContext>();

        this.ConfigureProvider(provider, optionsBuilder);

        await using var context = new ItemsContext(optionsBuilder.Options);

        try
        {
            await context.Database.ExecuteSqlRawAsync(commandText);
        }
        catch
        {
            // Ignore
        }

        Assert.NotEmpty(activities);

        // All activities should either be the root EFCore activity or a child of it.
        Assert.All(activities, activity => Assert.Equal(ActivitySourceName, (activity.Parent?.Source ?? activity.Source).Name));

        var activity = activities.Last();

        Assert.Equal(expectedSourceName, activity.Source.Name);

        VerifyActivityData(
            captureTextCommandContent,
            isFailure,
            expectedSystemName,
            expectedDatabaseName,
            activity);

        if (isFailure)
        {
            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Equal("Divide by zero error encountered.", activity.StatusDescription);
        }

        Assert.True(filtered);
    }

    [EnabledOnDockerPlatformTheory(EnabledOnDockerPlatformTheoryAttribute.DockerPlatform.Linux)]
    [MemberData(nameof(DataContextTestCases))]
    public async Task TracesDataContext(
        string provider,
        bool captureTextCommandContent,
        bool shouldEnrich,
        Type expectedCommandType,
        string expectedSystemName,
        string expectedDatabaseName)
    {
        var enriched = false;

        void ActivityEnrichment(Activity activity, IDbCommand command)
        {
            enriched = true;

            activity.SetTag("enriched", "yes");
            Assert.IsType(expectedCommandType, command, false);
        }

        var filtered = false;

        bool ActivityFilter(string? providerName, IDbCommand command)
        {
            filtered = true;

            Assert.True(providerName == provider || providerName == null, $"The provider name {providerName} is not null or the expected value.");
            Assert.IsType(expectedCommandType, command, false);

            return true;
        }

        var activities = new List<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddInMemoryExporter(activities)
            .AddSqlClientInstrumentation(options =>
            {
                options.SetDbStatementForText = captureTextCommandContent;
            })
            .AddEntityFrameworkCoreInstrumentation(options =>
            {
                options.Filter = ActivityFilter;
                options.SetDbStatementForText = captureTextCommandContent;
                if (shouldEnrich)
                {
                    options.EnrichWithIDbCommand = ActivityEnrichment;
                }
            })
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<ItemsContext>();

        this.ConfigureProvider(provider, optionsBuilder);

        await using var context = new ItemsContext(optionsBuilder.Options);
        await context.Database.EnsureCreatedAsync();

        // Clear activities from creating the database
        activities.Clear();

        var result = await context.Items.ToListAsync();

        Assert.NotNull(result);
        Assert.Empty(result);

        // All activities should either be the root EFCore activity or a child of it.
        Assert.All(activities, activity => Assert.Equal(ActivitySourceName, (activity.Parent?.Source ?? activity.Source).Name));

        // When using SQL Server there may be multiple activities, but we care
        // about the EFCore activity which should be the parent activity.
        var activity = Assert.Single(activities, activity => activity.Parent is null);

        Assert.Equal(ActivitySourceName, activity.Source.Name);
        Assert.Null(activity.Parent);

        Assert.Equal(expectedSystemName, activity.GetTagValue(SemanticConventions.AttributeDbSystem));
        Assert.Equal(expectedDatabaseName, activity.GetTagValue(SemanticConventions.AttributeDbName));

        Assert.Equal(shouldEnrich, enriched);
        Assert.True(filtered);
    }

    private static void VerifyActivityData(
        bool captureTextCommandContent,
        bool isFailure,
        string expectedSystemName,
        string expectedDatabaseName,
        Activity activity)
    {
        Assert.Equal(expectedDatabaseName, activity.DisplayName);

        Assert.Equal(ActivityKind.Client, activity.Kind);

        if (!isFailure)
        {
            Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        }
        else
        {
            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.NotNull(activity.StatusDescription);
            Assert.Empty(activity.Events);
        }

        Assert.Equal(expectedSystemName, activity.GetTagValue(SemanticConventions.AttributeDbSystem));
        Assert.Equal(expectedDatabaseName, activity.GetTagValue(SemanticConventions.AttributeDbName));

        if (captureTextCommandContent)
        {
            Assert.NotNull(activity.GetTagValue(SemanticConventions.AttributeDbStatement));
        }
        else
        {
            Assert.Null(activity.GetTagValue(SemanticConventions.AttributeDbStatement));
            Assert.Null(activity.GetTagValue(SemanticConventions.AttributeDbQueryText));
        }
    }

    private string GetConnectionString() => this.fixture.DatabaseContainer switch
    {
        SqlEdgeContainer container => container.GetConnectionString(),
        MsSqlContainer container => container.GetConnectionString(),
        _ => throw new InvalidOperationException($"Container type '${this.fixture.DatabaseContainer.GetType().Name}' is not supported."),
    };

    private void ConfigureProvider(string provider, DbContextOptionsBuilder<ItemsContext> builder)
    {
        switch (provider)
        {
            case "Microsoft.EntityFrameworkCore.Sqlite":
                var file = Path.GetTempFileName();
                builder.UseSqlite($"Filename={file}");
                break;

            case "Microsoft.EntityFrameworkCore.SqlServer":
                builder.UseSqlServer(this.GetConnectionString());
                break;

            default:
                throw new NotSupportedException($"Unsupported provider: {provider}");
        }
    }
}
