using System;
using System.Net;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class CacheDurationTest
{
  [Fact]
  public void Union_NullOther_ReturnsReceiverItself()
  {
    var baseline = new CacheDuration();
    Assert.Same(baseline, baseline.Union(null));
  }

  [Fact]
  public void Union_RightSideWins_AndPreservesBaselineForUnspecified()
  {
    var baseline = new CacheDuration(); // OK = 5 min, NotFound = 5 min
    var overrides = new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromMinutes(30),            // overrides baseline OK
      [HttpStatusCode.InternalServerError] = TimeSpan.FromMinutes(10), // adds a new status
    };

    var union = baseline.Union(overrides);

    Assert.Equal(TimeSpan.FromMinutes(30), union.GetDuration(HttpStatusCode.OK));            // right wins
    Assert.Equal(TimeSpan.FromMinutes(5), union.GetDuration(HttpStatusCode.NotFound));        // baseline kept
    Assert.Equal(TimeSpan.FromMinutes(10), union.GetDuration(HttpStatusCode.InternalServerError)); // added
  }

  [Fact]
  public void Union_DoesNotMutateEitherOperand()
  {
    // Regression guard: Union must build a fresh result and leave both operands untouched.
    var baseline = new CacheDuration { [HttpStatusCode.OK] = TimeSpan.FromMinutes(5) };
    var overrides = new CacheDuration { [HttpStatusCode.OK] = TimeSpan.FromMinutes(30) };

    baseline.Union(overrides);

    Assert.Equal(TimeSpan.FromMinutes(5), baseline[HttpStatusCode.OK]);
    Assert.Equal(TimeSpan.FromMinutes(30), overrides[HttpStatusCode.OK]);
  }

  [Fact]
  public void GetDuration_UnknownStatus_DefaultsToOneMinute()
  {
    // BadGateway is in neither the defaults nor any override.
    Assert.Equal(TimeSpan.FromMinutes(1), new CacheDuration().GetDuration(HttpStatusCode.BadGateway));
  }
}
