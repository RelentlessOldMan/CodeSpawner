using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// Classes that swap the process-wide <see cref="Console"/> to capture a run's output. Sharing one collection
/// keeps them from swapping it under each other; <c>DisableParallelization</c> also keeps every other test class
/// from running meanwhile, since the generator, scan and mutate all print, and a parallel test's lines would land
/// in the capture (or race on its unsynchronized writer).
/// </summary>
[CollectionDefinition("CapturesConsole", DisableParallelization = true)]
public sealed class CapturesConsoleCollection;
