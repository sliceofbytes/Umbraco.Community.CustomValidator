using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Umbraco.Community.CustomValidator.Enums;
using Umbraco.Community.CustomValidator.Services;

namespace Umbraco.Community.CustomValidator.Tests.Services;

[TestFixture]
public sealed class CustomValidationStatusCacheTests
{
    private MemoryCache _memoryCache = null!;
    private Mock<ILogger<CustomValidationStatusCache>> _loggerMock = null!;
    private Mock<IOptions<CustomValidatorOptions>> _optionsMock = null!;
    private CustomValidatorOptions _options = null!;
    private CustomValidationStatusCache _sut = null!;

    [SetUp]
    public void Setup()
    {
        _options = new CustomValidatorOptions
        {
            CacheExpirationMinutes = 30
        };

        _optionsMock = new Mock<IOptions<CustomValidatorOptions>>();
        _optionsMock.Setup(x => x.Value).Returns(_options);

        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _loggerMock = new Mock<ILogger<CustomValidationStatusCache>>();

        _sut = new CustomValidationStatusCache(_memoryCache, _optionsMock.Object, _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _memoryCache.Dispose();
    }

    #region GetStatus Tests

    [Test]
    public void GetStatus_WhenNothingCached_ReturnsUnknown()
    {
        var result = _sut.GetStatus(Guid.NewGuid());

        Assert.That(result, Is.EqualTo(ValidationStatus.Unknown));
    }

    [Test]
    public void GetStatus_AfterSetStatus_ReturnsCachedValue()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, ValidationStatus.HasErrors);
        var result = _sut.GetStatus(documentId);

        Assert.That(result, Is.EqualTo(ValidationStatus.HasErrors));
    }

    [Test]
    public void GetStatus_WithCulture_ReturnsCachedValueForCulture()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, ValidationStatus.Valid, "en-US");
        var result = _sut.GetStatus(documentId, "en-US");

        Assert.That(result, Is.EqualTo(ValidationStatus.Valid));
    }

    [Test]
    public void GetStatus_WithDifferentCulture_ReturnsUnknown()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, ValidationStatus.HasErrors, "en-US");
        var result = _sut.GetStatus(documentId, "da-DK");

        Assert.That(result, Is.EqualTo(ValidationStatus.Unknown));
    }

    [Test]
    public void GetStatus_NullAndEmptyCulture_TreatedAsSameKey()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, ValidationStatus.HasErrors, null);
        var resultWithEmpty = _sut.GetStatus(documentId, string.Empty);
        var resultWithNull = _sut.GetStatus(documentId, null);

        Assert.That(resultWithEmpty, Is.EqualTo(ValidationStatus.HasErrors));
        Assert.That(resultWithNull, Is.EqualTo(ValidationStatus.HasErrors));
    }

    [Test]
    public void GetStatus_DifferentDocuments_IndependentCaches()
    {
        var doc1 = Guid.NewGuid();
        var doc2 = Guid.NewGuid();

        _sut.SetStatus(doc1, ValidationStatus.HasErrors);
        _sut.SetStatus(doc2, ValidationStatus.Valid);

        Assert.That(_sut.GetStatus(doc1), Is.EqualTo(ValidationStatus.HasErrors));
        Assert.That(_sut.GetStatus(doc2), Is.EqualTo(ValidationStatus.Valid));
    }

    #endregion

    #region SetStatus (ValidationStatus overload) Tests

    [Test]
    public void SetStatus_ValidationStatus_StoresHasErrors()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, ValidationStatus.HasErrors);

        Assert.That(_sut.GetStatus(documentId), Is.EqualTo(ValidationStatus.HasErrors));
    }

    [Test]
    public void SetStatus_ValidationStatus_StoresValid()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, ValidationStatus.Valid);

        Assert.That(_sut.GetStatus(documentId), Is.EqualTo(ValidationStatus.Valid));
    }

    [Test]
    public void SetStatus_Overwrites_PreviousStatus()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, ValidationStatus.HasErrors);
        _sut.SetStatus(documentId, ValidationStatus.Valid);

        Assert.That(_sut.GetStatus(documentId), Is.EqualTo(ValidationStatus.Valid));
    }

    [Test]
    public void SetStatus_WhenCacheExpirationZero_StatusNotStored()
    {
        _options.CacheExpirationMinutes = 0;
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, ValidationStatus.HasErrors);

        Assert.That(_sut.GetStatus(documentId), Is.EqualTo(ValidationStatus.Unknown));
    }

    [Test]
    public void SetStatus_WhenCacheExpirationNegative_StatusNotStored()
    {
        _options.CacheExpirationMinutes = -1;
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, ValidationStatus.Valid);

        Assert.That(_sut.GetStatus(documentId), Is.EqualTo(ValidationStatus.Unknown));
    }

    #endregion

    #region SetStatus (bool overload) Tests

    [Test]
    public void SetStatus_Bool_TrueMapsToHasErrors()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, hasErrors: true);

        Assert.That(_sut.GetStatus(documentId), Is.EqualTo(ValidationStatus.HasErrors));
    }

    [Test]
    public void SetStatus_Bool_FalseMapsToValid()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, hasErrors: false);

        Assert.That(_sut.GetStatus(documentId), Is.EqualTo(ValidationStatus.Valid));
    }

    [Test]
    public void SetStatus_Bool_WithCulture_StoresForCulture()
    {
        var documentId = Guid.NewGuid();

        _sut.SetStatus(documentId, hasErrors: true, "da-DK");

        Assert.That(_sut.GetStatus(documentId, "da-DK"), Is.EqualTo(ValidationStatus.HasErrors));
        Assert.That(_sut.GetStatus(documentId, null), Is.EqualTo(ValidationStatus.Unknown));
    }

    #endregion

    #region ClearStatus Tests

    [Test]
    public void ClearStatus_RemovesSpecificCultureEntry()
    {
        var documentId = Guid.NewGuid();
        _sut.SetStatus(documentId, ValidationStatus.HasErrors, "en-US");
        _sut.SetStatus(documentId, ValidationStatus.HasErrors, "da-DK");

        _sut.ClearStatus(documentId, "en-US");

        Assert.That(_sut.GetStatus(documentId, "en-US"), Is.EqualTo(ValidationStatus.Unknown));
        Assert.That(_sut.GetStatus(documentId, "da-DK"), Is.EqualTo(ValidationStatus.HasErrors));
    }

    [Test]
    public void ClearStatus_NullCulture_RemovesInvariantEntry()
    {
        var documentId = Guid.NewGuid();
        _sut.SetStatus(documentId, ValidationStatus.HasErrors, null);

        _sut.ClearStatus(documentId, null);

        Assert.That(_sut.GetStatus(documentId, null), Is.EqualTo(ValidationStatus.Unknown));
    }

    [Test]
    public void ClearStatus_WhenNotSet_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _sut.ClearStatus(Guid.NewGuid(), "en-US"));
    }

    #endregion

    #region ClearForDocument Tests

    [Test]
    public void ClearForDocument_RemovesAllCulturesForDocument()
    {
        var documentId = Guid.NewGuid();
        _sut.SetStatus(documentId, ValidationStatus.HasErrors, "en-US");
        _sut.SetStatus(documentId, ValidationStatus.HasErrors, "da-DK");
        _sut.SetStatus(documentId, ValidationStatus.HasErrors, null);

        _sut.ClearForDocument(documentId);

        Assert.That(_sut.GetStatus(documentId, "en-US"), Is.EqualTo(ValidationStatus.Unknown));
        Assert.That(_sut.GetStatus(documentId, "da-DK"), Is.EqualTo(ValidationStatus.Unknown));
        Assert.That(_sut.GetStatus(documentId, null), Is.EqualTo(ValidationStatus.Unknown));
    }

    [Test]
    public void ClearForDocument_DoesNotAffectOtherDocuments()
    {
        var doc1 = Guid.NewGuid();
        var doc2 = Guid.NewGuid();
        _sut.SetStatus(doc1, ValidationStatus.HasErrors, "en-US");
        _sut.SetStatus(doc2, ValidationStatus.HasErrors, "en-US");

        _sut.ClearForDocument(doc1);

        Assert.That(_sut.GetStatus(doc1, "en-US"), Is.EqualTo(ValidationStatus.Unknown));
        Assert.That(_sut.GetStatus(doc2, "en-US"), Is.EqualTo(ValidationStatus.HasErrors));
    }

    [Test]
    public void ClearForDocument_WhenNothingCached_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _sut.ClearForDocument(Guid.NewGuid()));
    }

    #endregion

    #region ClearAll Tests

    [Test]
    public void ClearAll_RemovesAllEntries()
    {
        var doc1 = Guid.NewGuid();
        var doc2 = Guid.NewGuid();
        _sut.SetStatus(doc1, ValidationStatus.HasErrors, "en-US");
        _sut.SetStatus(doc2, ValidationStatus.Valid, "da-DK");

        _sut.ClearAll();

        Assert.That(_sut.GetStatus(doc1, "en-US"), Is.EqualTo(ValidationStatus.Unknown));
        Assert.That(_sut.GetStatus(doc2, "da-DK"), Is.EqualTo(ValidationStatus.Unknown));
    }

    [Test]
    public void ClearAll_WhenEmpty_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _sut.ClearAll());
    }

    [Test]
    public void ClearAll_AllowsNewEntriesAfterClear()
    {
        var documentId = Guid.NewGuid();
        _sut.SetStatus(documentId, ValidationStatus.HasErrors);
        _sut.ClearAll();

        _sut.SetStatus(documentId, ValidationStatus.Valid);

        Assert.That(_sut.GetStatus(documentId), Is.EqualTo(ValidationStatus.Valid));
    }

    #endregion
}
