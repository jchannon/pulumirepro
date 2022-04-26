// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.App;

using Insights = Pulumi.AzureNative.OperationalInsights;

await Pulumi.Deployment.RunAsync<ContainerAppsEnvironmentStack>();

class ContainerAppsEnvironmentStack : Pulumi.Stack {
    public ContainerAppsEnvironmentStack() {
        var options = ContainerAppsEnvironmentOptions.Load();

        var resourceGroup = new ResourceGroup(
            options.ResourceGroup,
            new ResourceGroupArgs {
                ResourceGroupName = options.ResourceGroup,
                Tags              = { { "environment", options.Environment } }
            }
        );

        var managedEnvironment = GetManagedEnvironment(resourceGroup, options);

        Id       = managedEnvironment.Id;
        Name     = managedEnvironment.Name;
        StaticIp = managedEnvironment.StaticIp;

        bool failed;

        do {
            try {
                

                failed = false;
            }
            catch (Exception ex) {
                failed = true;
                Log.Exception(ex);
            }
        } while (failed);

        static ManagedEnvironment GetManagedEnvironment(ResourceGroup resourceGroup, ContainerAppsEnvironmentOptions options) {
            var workspaceName = $"{options.Environment}-logs";

            var workspace = new Insights.Workspace(
                workspaceName,
                new() {
                    WorkspaceName     = workspaceName,
                    ResourceGroupName = resourceGroup.Name,
                    Sku               = new Insights.Inputs.WorkspaceSkuArgs { Name = "PerGB2018" },
                    RetentionInDays   = options.LogsRetentionDays,
                    Tags              = { { "environment", options.Environment } }
                }
            );

            var sharedKeys = Output.Tuple(resourceGroup.Name, workspace.Name).Apply(
                items => Insights.GetSharedKeys.InvokeAsync(
                    new() {
                        ResourceGroupName = items.Item1,
                        WorkspaceName     = items.Item2
                    }
                )
            );

            var environmentName = options.Environment;

            return new ManagedEnvironment(
                environmentName,
                new() {
                    Name              = environmentName,
                    ResourceGroupName = resourceGroup.Name,
                    AppLogsConfiguration = new AppLogsConfigurationArgs {
                        Destination = "log-analytics",
                        LogAnalyticsConfiguration = new LogAnalyticsConfigurationArgs {
                            CustomerId = workspace.CustomerId,
                            SharedKey  = sharedKeys.Apply(r => r.PrimarySharedKey)!
                        }
                    },
                    Tags = { { "environment", options.Environment } }
                }, new CustomResourceOptions {
                    CustomTimeouts = new CustomTimeouts {
                        Create = TimeSpan.FromMinutes(5),
                        Update = TimeSpan.FromMinutes(5),
                        Delete = TimeSpan.FromMinutes(5)
                    }
                }
            );
        }
    }

    [Output] public Output<string> Id       { get; set; }
    [Output] public Output<string> Name     { get; set; }
    [Output] public Output<string> StaticIp { get; set; }
}

record ContainerAppsEnvironmentOptions {
    public string ResourceGroup     { get; init; } = null!;
    public string Environment       { get; init; } = null!;
    public int    LogsRetentionDays { get; init; }
    
    public static ContainerAppsEnvironmentOptions Load() {
        var validEnvNames = new[] {
            "production",
            "staging",
            "development"
        };

        // by convention only production, staging and development are accepted
        var environment = Pulumi.Deployment.Instance.StackName.ToLower();

        if (!validEnvNames.Contains(environment))
            throw new Exception("Invalid environment name!");

        var config = new Config();

        var options = new ContainerAppsEnvironmentOptions {
            ResourceGroup     = config.Require("resource-group"),
            Environment       = environment,
            LogsRetentionDays = config.RequireInt32("logs-retention-days")
        };

        return options;
    }
}