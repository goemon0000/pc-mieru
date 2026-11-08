// Strings.Current is process-wide state that CliApp.RunAsync sets, so these tests run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
