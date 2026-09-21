using Xunit;

// Integration tests start real transfer sessions that find each other over local multicast. Tests that share game content
// would otherwise find each other's peers and add load to each other, which makes timing based tests unreliable.
// Running them one at a time is slower and deterministic. Production runs one session per process.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
