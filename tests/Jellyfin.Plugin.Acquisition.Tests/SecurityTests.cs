using System.Security.Claims;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Security;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class SecurityTests
{
    [Fact]
    public void GetCurrentUserId_ReadsJellyfinUserIdClaim_Successfully()
    {
        var expectedId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim("Jellyfin-UserId", expectedId.ToString()),
            new Claim(ClaimTypes.Name, "Alice")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var userId = SecurityHelper.GetCurrentUserId(principal);
        Assert.Equal(expectedId, userId);
    }

    [Fact]
    public void GetCurrentUserId_RejectsEmptyGuid()
    {
        var claims = new[]
        {
            new Claim("Jellyfin-UserId", Guid.Empty.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var userId = SecurityHelper.GetCurrentUserId(principal);
        Assert.Null(userId);
    }

    [Fact]
    public void CanAcquire_RejectsNonAuthorizedUser_WhenAllowAllIsFalse()
    {
        var userId = Guid.NewGuid();
        var mockUserManager = new Mock<IUserManager>();

        var mockUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "testuser", "testuser");
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);

        var config = new PluginConfiguration
        {
            AllowAllUsers = false,
            AllowedUserIds = []
        };

        var result = SecurityHelper.CanAcquire(userId, mockUserManager.Object, config);
        Assert.False(result);
    }

    [Fact]
    public void CanManageRequest_UserBCannotManageUserARequest()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var mockUserManager = new Mock<IUserManager>();
        var mockUserB = new Jellyfin.Database.Implementations.Entities.User("userB", "userB", "userB");
        mockUserManager.Setup(u => u.GetUserById(userB)).Returns(mockUserB);

        var config = new PluginConfiguration { AllowAllUsers = true };
        var request = new AcquisitionRequest
        {
            UserId = userA,
            Title = "Some Title"
        };

        var result = SecurityHelper.CanManageRequest(userB, request, mockUserManager.Object, config);
        Assert.False(result);
    }

    [Fact]
    public void ReleaseCandidateDto_OmitsDownloadUrlAndMagnetUri()
    {
        var candidate = new ReleaseCandidate
        {
            Id = "cand-1",
            Title = "Test.Movie.1080p",
            DownloadUrl = "http://prowlarr:9696/api/v1/download?apikey=SUPERSECRET123",
            MagnetUri = "magnet:?xt=urn:btih:abc&tr=http://tracker.com/passkey123",
            Size = 2_000_000_000L,
            Resolution = "1080p",
            Source = "WEB-DL",
            Codec = "H.264"
        };

        var dto = ReleaseCandidateDto.FromModel(candidate);

        Assert.Equal("cand-1", dto.Id);
        Assert.Equal("Test.Movie.1080p", dto.Title);

        // Verify DTO does not contain DownloadUrl or MagnetUri
        var dtoProps = typeof(ReleaseCandidateDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("DownloadUrl", dtoProps);
        Assert.DoesNotContain("MagnetUri", dtoProps);
    }

    [Theory]
    [InlineData("http://host:9696/download?apikey=12345abcde&other=1", "http://host:9696/download?apikey=[REDACTED]&other=1")]
    [InlineData("token=SECRET_TOKEN_XYZ", "token=[REDACTED]")]
    [InlineData("http://user:password123@domain.com/feed", "http://user:[REDACTED]@domain.com/feed")]
    public void RedactSecrets_RedactsSensitiveTokens(string input, string expected)
    {
        var redacted = SecurityHelper.RedactSecrets(input);
        Assert.Equal(expected, redacted);
    }
}
