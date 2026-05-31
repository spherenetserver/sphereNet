using Xunit;

// The engine exposes process-wide mutable statics that nearly every test
// re-points in its CreateWorld helper (ObjBase.ResolveWorld, Item.ResolveWorld,
// VendorEngine.World). Running test collections in parallel lets one class
// clobber another's world reference mid-test, producing intermittent failures
// (and red CI). The suite runs in a couple of seconds, so serialize the whole
// assembly; per-test setup re-establishes the statics so order is irrelevant.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
