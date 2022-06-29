// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

using System;
using Pulumi;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.ContainerRegistry.Inputs;
using Pulumi.AzureNative.OperationalInsights.Inputs;
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

        // var managedEnvironmentId = managedEnvironment.Id;
        //
        // var registry = GetRegistry(options.ResourceGroup, options.RegistryName);
        //
        // var registries = new InputList<RegistryCredentialsArgs> {
        //     new RegistryCredentialsArgs {
        //         Server            = registry.Server,
        //         Username          = registry.Username,
        //         PasswordSecretRef = "registry-password"
        //     }, new InputList<RegistryCredentialsArgs> {
        //         new RegistryCredentialsArgs {
        //             Server = "docker.io"
        //         }
        //     }
        // };
        //
        // var datadogAgentApp = DeployDatadogAgent(
        //     options,
        //     resourceGroup,
        //     managedEnvironmentId,
        //     registries
        // );
        //
        // DatadogAgentUrl = Output.Format(
        //     $"https://{datadogAgentApp.Configuration.Apply(x => x!.Ingress).Apply(x => x!.Fqdn)}"
        // );
        //
        // DatadogAgentRevision = datadogAgentApp.LatestRevisionName;

        static ManagedEnvironment GetManagedEnvironment(
            ResourceGroup resourceGroup, ContainerAppsEnvironmentOptions options
        ) {
            var workspaceName = $"{options.Environment}-logs";

            var workspace = new Insights.Workspace(
                workspaceName,
                new() {
                    WorkspaceName     = workspaceName,
                    ResourceGroupName = resourceGroup.Name,
                    Sku               = new WorkspaceSkuArgs { Name = "PerGB2018" },
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
                    EnvironmentName = environmentName,
                    ResourceGroupName = resourceGroup.Name,
                    AppLogsConfiguration = new AppLogsConfigurationArgs {
                        Destination = "log-analytics",
                        LogAnalyticsConfiguration = new LogAnalyticsConfigurationArgs {
                            CustomerId = workspace.CustomerId,
                            SharedKey  = sharedKeys.Apply(r => r.PrimarySharedKey)!
                        }
                    },
                    Tags = { { "environment", options.Environment } }
                },
                new CustomResourceOptions {
                    CustomTimeouts = new CustomTimeouts {
                        Create = TimeSpan.FromMinutes(5),
                        Update = TimeSpan.FromMinutes(5),
                        Delete = TimeSpan.FromMinutes(5)
                    }
                }
            );
        }

        static ContainerApp DeployDatadogAgent(
            ContainerAppsEnvironmentOptions options, ResourceGroup resourceGroup, Output<string> managedEnvironmentId,
            InputList<RegistryCredentialsArgs> registries
        ) {
            var revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

            var app = new ContainerApp(
                $"{options.Environment}-datadog-agent",
                new() {
                    ContainerAppName =  $"{options.Environment}-datadog-agent",
                    ResourceGroupName    = resourceGroup.Name,
                    ManagedEnvironmentId = managedEnvironmentId,
                    Configuration = new ConfigurationArgs {
                        Ingress = new IngressArgs {
                            Transport     = IngressTransportMethod.Http,
                            TargetPort    = 4343,
                            External      = false,
                            AllowInsecure = true,
                            Traffic = new TrafficWeightArgs {
                                Weight         = 100,
                                LatestRevision = true
                            }
                        },
                        Registries = registries,
                        //Secrets             = secretArgs,
                        ActiveRevisionsMode = ActiveRevisionsMode.Single
                    },
                    Template = new TemplateArgs {
                        RevisionSuffix = revision,
                        Containers = {
                            new ContainerArgs {
                                Name  = $"{options.Environment}-datadog-agent",
                                Image = "datadog/agent",
                                //Env   = environmentVarArgs,
                                Resources = new ContainerResourcesArgs {
                                    Cpu    = 0.5,
                                    Memory = "0.5Gi"
                                },
                                /*Probes = {
                                    new ContainerAppProbeArgs {
                                        HttpGet = new ContainerAppProbeHttpGetArgs {
                                            Path = "/health",
                                            Port = settings.Ingress.TargetPort
                                        },
                                        InitialDelaySeconds = 3,
                                        PeriodSeconds       = 3,
                                        Type                = "liveness"
                                    }
                                }*/
                            }
                        },
                        Scale = new ScaleArgs {
                            MaxReplicas = 1,
                            MinReplicas = 1
                            // , Rules = new ScaleRuleArgs {
                            //     Custom = new AzureNative.App.Inputs.CustomScaleRuleArgs {
                            //         Metadata = {
                            //             { "concurrentRequests", "50" },
                            //         },
                            //         Type = "http",
                            //     },
                            //     Http = new HttpScaleRuleArgs {
                            //         Auth     = null,
                            //         Metadata = null
                            //     },
                            //     Name = "httpscalingrule"
                            // }
                        }
                    },
                    Tags = {
                        { "environment", options.Environment },
                        //{ "version", settings.Version },
                        { "provisioned-by", "pulumi" }
                    }
                }
            );

            return app;
        }

        static (Output<string> Server, Output<string> Username, Output<string> Password) GetRegistry(
            string resourceGroupName, string registryName
        ) {
            var registry = new Registry(registryName, new RegistryArgs
            {
                ResourceGroupName = resourceGroupName,
                Sku               = new SkuArgs { Name = "Basic" },
                AdminUserEnabled  = true,
                RegistryName = registryName
            });
        
            var credentials = Output.Tuple<string,string>(resourceGroupName, registry.Name).Apply(items =>
                                                                                       ListRegistryCredentials.InvokeAsync(new ListRegistryCredentialsArgs
                                                                                       {
                                                                                           ResourceGroupName = items.Item1,
                                                                                           RegistryName = items.Item2
                                                                                       }));
            var adminUsername = credentials.Apply(credentials => credentials.Username);
            var adminPassword = credentials.Apply(credentials => credentials.Passwords[0].Value);

            var loginServer = registry.LoginServer; // registry.Apply(x => x.LoginServer);
            

            return (loginServer, adminUsername, adminPassword);
        }
    }

    [Output] public Output<string> Id       { get; set; }
    [Output] public Output<string> Name     { get; set; }
    [Output] public Output<string> StaticIp { get; set; }

    [Output] public Output<string> DatadogAgentRevision { get; private set; }
    [Output] public Output<string> DatadogAgentUrl      { get; private set; }
}

record ContainerAppsEnvironmentOptions
{
    public string ResourceGroup     { get; init; } = null!;
    public string Environment       { get; init; } = null!;
    public int    LogsRetentionDays { get; init; }

    public string RegistryName { get; init; } = null!;

    static readonly string[] ValidEnvNames = { "production", "staging", "development" };

    public static ContainerAppsEnvironmentOptions Load() {
        // by convention only production, staging and development are accepted
        var environment = Pulumi.Deployment.Instance.StackName.ToLower();

        if (!ValidEnvNames.Contains(environment))
            throw new Exception("Invalid environment name!");

        var config = new Config();

        var options = new ContainerAppsEnvironmentOptions {
            ResourceGroup     = config.Require("resource-group"),
            Environment       = environment,
            LogsRetentionDays = config.RequireInt32("logs-retention-days"),
            RegistryName      = config.Require("registry-name")
        };

        return options;
    }
}