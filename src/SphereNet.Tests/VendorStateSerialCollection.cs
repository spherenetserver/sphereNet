namespace SphereNet.Tests;

// VendorEngine.World is a shared static read inside ProcessBuy. Classes that
// drive vendor trades must not run in parallel or one clobbers the other's
// world reference mid-call (NullReferenceException). Serialize them.
[CollectionDefinition("VendorStateSerial", DisableParallelization = true)]
public class VendorStateSerialCollection;
