using Xunit;

// Plugin.Instance is a process-wide static that many tests replace; running test classes in parallel lets
// one test observe another test's configuration. The suite is small, so it runs sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
