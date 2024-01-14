using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nop.Core;
using Nop.Core.Domain.Common;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Domain.ScheduleTasks;
using Nop.Plugin.Payments.Square.Components;
using Nop.Plugin.Payments.Square.Domain;
using Nop.Plugin.Payments.Square.Extensions;
using Nop.Plugin.Payments.Square.Models;
using Nop.Plugin.Payments.Square.Services;
using Nop.Services.Common;
using Nop.Services.Configuration;
using Nop.Services.Customers;
using Nop.Services.Directory;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Plugins;
using Nop.Services.ScheduleTasks;
using Nop.Web.Framework.UI;
using SquareModel = Square.Models;

namespace Nop.Plugin.Payments.Square;

/// <summary>
/// Represents Square payment method
/// </summary>
public class SquarePaymentMethod : BasePlugin, IPaymentMethod
{
    #region Fields

    private readonly CurrencySettings _currencySettings;
    private readonly ICountryService _countryService;
    private readonly ICurrencyService _currencyService;
    private readonly ICustomerService _customerService;
    private readonly IGenericAttributeService _genericAttributeService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger _logger;
    private readonly IOrderTotalCalculationService _orderTotalCalculationService;
    private readonly INopHtmlHelper _nopHtmlHelper;
    private readonly ISettingService _settingService;
    private readonly IScheduleTaskService _scheduleTaskService;
    private readonly IStateProvinceService _stateProvinceService;
    private readonly IWebHelper _webHelper;
    private readonly SquarePaymentManager _squarePaymentManager;
    private readonly SquarePaymentSettings _squarePaymentSettings;
    private readonly IStoreContext _storeContext;

    #endregion

    #region Ctor

    public SquarePaymentMethod(CurrencySettings currencySettings,
        ICountryService countryService,
        ICurrencyService currencyService,
        ICustomerService customerService,
        IGenericAttributeService genericAttributeService,
        ILocalizationService localizationService,
        ILogger logger,
        IOrderTotalCalculationService orderTotalCalculationService,
        INopHtmlHelper nopHtmlHelper,
        ISettingService settingService,
        IScheduleTaskService scheduleTaskService,
        IStateProvinceService stateProvinceService,
        IWebHelper webHelper,
        SquarePaymentManager squarePaymentManager,
        SquarePaymentSettings squarePaymentSettings,
        IStoreContext storeContext)
    {
        _currencySettings = currencySettings;
        _countryService = countryService;
        _currencyService = currencyService;
        _customerService = customerService;
        _genericAttributeService = genericAttributeService;
        _localizationService = localizationService;
        _logger = logger;
        _orderTotalCalculationService = orderTotalCalculationService;
        _nopHtmlHelper = nopHtmlHelper;
        _settingService = settingService;
        _scheduleTaskService = scheduleTaskService;
        _stateProvinceService = stateProvinceService;
        _webHelper = webHelper;
        _squarePaymentManager = squarePaymentManager;
        _squarePaymentSettings = squarePaymentSettings;
        _storeContext = storeContext;
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Check supported currency 
    /// </summary>
    /// <param name="currency">Currency</param>
    /// <returns>True - value must be correspond to ISO 4217, else - false</returns>
    private bool CheckSupportCurrency(Currency currency)
    {
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            var regionInfo = new RegionInfo(culture.Name);
            if (currency.CurrencyCode.Equals(regionInfo.ISOCurrencySymbol, StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets a payment status by tender card details status
    /// </summary>
    /// <param name="status">Tender card details status</param>
    /// <returns>Payment status</returns>
    private PaymentStatus GetPaymentStatus(string status)
    {
        return status switch
        {
            SquarePaymentDefaults.Status.PAYMENT_APPROVED => PaymentStatus.Authorized,
            SquarePaymentDefaults.Status.PAYMENT_COMPLETED => PaymentStatus.Paid,
            SquarePaymentDefaults.Status.PAYMENT_FAILED => PaymentStatus.Pending,
            SquarePaymentDefaults.Status.PAYMENT_CANCELED => PaymentStatus.Voided,
            _ => PaymentStatus.Pending
        };
    }

    /// <summary>
    /// Process a payment
    /// </summary>
    /// <param name="paymentRequest">Payment info required for an order processing</param>
    /// <param name="isRecurringPayment">Whether it is a recurring payment</param>
    /// <returns>The asynchronous task whose result contains the Process payment result</returns>
    private async Task<ProcessPaymentResult> ProcessPaymentAsync(ProcessPaymentRequest paymentRequest, bool isRecurringPayment)
    {
        //create charge request
        var squarePaymentRequest = await CreatePaymentRequestAsync(paymentRequest, isRecurringPayment);

        //charge transaction for current store
        var storeId = (await _storeContext.GetCurrentStoreAsync()).Id;
        var (payment, error) = await _squarePaymentManager.CreatePaymentAsync(squarePaymentRequest, storeId);
        if (payment == null)
            throw new NopException(error);

        //get transaction details
        var paymentStatus = payment.Status;
        var paymentResult = $"Payment was processed. Status is {paymentStatus}";

        //return result
        var result = new ProcessPaymentResult
        {
            NewPaymentStatus = GetPaymentStatus(paymentStatus)
        };

        switch (_squarePaymentSettings.TransactionMode)
        {
            case TransactionMode.Authorize:
                result.AuthorizationTransactionId = payment.Id;
                result.AuthorizationTransactionResult = paymentResult;
                break;
            case TransactionMode.Charge:
                result.CaptureTransactionId = payment.Id;
                result.CaptureTransactionResult = paymentResult;
                break;
        }

        return result;
    }

    /// <summary>
    /// Create request parameters to charge transaction
    /// </summary>
    /// <param name="paymentRequest">Payment request parameters</param>
    /// <param name="isRecurringPayment">Whether it is a recurring payment</param>
    /// <returns>The asynchronous task whose result contains the Charge request parameters</returns>
    private async Task<ExtendedCreatePaymentRequest> CreatePaymentRequestAsync(ProcessPaymentRequest paymentRequest, bool isRecurringPayment)
    {
        //get customer
        var customer = await _customerService.GetCustomerByIdAsync(paymentRequest.CustomerId);
        if (customer == null)
            throw new NopException("Customer cannot be loaded");

        //get the primary store currency
        var currency = await _currencyService.GetCurrencyByIdAsync(_currencySettings.PrimaryStoreCurrencyId);
        if (currency == null)
            throw new NopException("Primary store currency cannot be loaded");

        //whether the currency is supported by the Square
        if (!CheckSupportCurrency(currency))
            throw new NopException($"The {currency.CurrencyCode} currency is not supported by the Square");

        var storeId = (await _storeContext.GetCurrentStoreAsync()).Id;

        //check customer's billing address, shipping address and email, 
        async Task<SquareModel.Address> createAddressAsync(Address address)
        {
            if (address == null)
                return null;

            var country = await _countryService.GetCountryByAddressAsync(address);

            return new SquareModel.Address
            (
                addressLine1: CommonHelper.EnsureMaximumLength(address.Address1, 500),
                addressLine2: CommonHelper.EnsureMaximumLength(address.Address2, 500),
                administrativeDistrictLevel1: CommonHelper.EnsureMaximumLength((await _stateProvinceService.GetStateProvinceByAddressAsync(address))?.Abbreviation, 200),
                administrativeDistrictLevel2: CommonHelper.EnsureMaximumLength(address.County, 200),
                country: string.Equals(country?.TwoLetterIsoCode, new RegionInfo(country?.TwoLetterIsoCode).TwoLetterISORegionName, StringComparison.InvariantCultureIgnoreCase)
                    ? country?.TwoLetterIsoCode : null,
                firstName: CommonHelper.EnsureMaximumLength(address.FirstName, 300),
                lastName: CommonHelper.EnsureMaximumLength(address.LastName, 300),
                locality: CommonHelper.EnsureMaximumLength(address.City, 300),
                postalCode: CommonHelper.EnsureMaximumLength(address.ZipPostalCode, 12)
            );
        }

        var customerBillingAddress = await _customerService.GetCustomerBillingAddressAsync(customer);
        var customerShippingAddress = await _customerService.GetCustomerShippingAddressAsync(customer);

        var billingAddress = await createAddressAsync(customerBillingAddress);
        var shippingAddress = billingAddress == null ? await createAddressAsync(customerShippingAddress) : null;
        var email = customerBillingAddress != null ? customerBillingAddress.Email : customerShippingAddress?.Email;

        //the transaction is ineligible for chargeback protection if they are not provided
        if ((billingAddress == null && shippingAddress == null) || string.IsNullOrEmpty(email))
            await _logger.WarningAsync("Square payment warning: Address or email is not provided, so the transaction is ineligible for chargeback protection", customer: customer);

        //the amount of money, in the smallest denomination of the currency indicated by currency. For example, when currency is USD, amount is in cents;
        //most currencies consist of 100 units of smaller denomination, so we multiply the total by 100
        var orderTotal = (int)(paymentRequest.OrderTotal * 100);
        var amountMoney = new SquareModel.Money(orderTotal, currency.CurrencyCode);

        //try to get the verification token if exists
        var tokenKey = await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.Token.Key");
        if ((!paymentRequest.CustomValues.TryGetValue(tokenKey, out var token) || string.IsNullOrEmpty(token?.ToString())) && _squarePaymentSettings.Use3ds)
            throw new NopException("Failed to get the verification token");

        //remove the verification token from payment custom values, since it's no longer needed
        paymentRequest.CustomValues.Remove(tokenKey);

        var location = await _squarePaymentManager.GetSelectedActiveLocationAsync(storeId);
        if (location == null)
            throw new NopException("Location is a required parameter for payment requests");

        var paymentRequestBuilder = new SquareModel.CreatePaymentRequest.Builder
            (
                //Payment source, regardless of whether it is a card on file or a nonce.
                //this parameter will be initialized below
                sourceId: null,
                idempotencyKey: Guid.NewGuid().ToString()
            ).AmountMoney(amountMoney)
            .Autocomplete(_squarePaymentSettings.TransactionMode == TransactionMode.Charge)
            .BillingAddress(billingAddress)
            .ShippingAddress(shippingAddress)
            .BuyerEmailAddress(email)
            .Note(string.Format(SquarePaymentDefaults.PaymentNote, paymentRequest.OrderGuid))
            .ReferenceId(paymentRequest.OrderGuid.ToString())
            .VerificationToken(token?.ToString())
            .LocationId(location.Id);

        var integrationId = !_squarePaymentSettings.UseSandbox && !string.IsNullOrEmpty(SquarePaymentDefaults.IntegrationId)
            ? SquarePaymentDefaults.IntegrationId
            : null;

        //try to get previously stored card details
        var storedCardKey = await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.StoredCard.Key");
        if (paymentRequest.CustomValues.TryGetValue(storedCardKey, out var storedCardId) && !storedCardId.ToString().Equals(Guid.Empty.ToString()))
        {
            //check whether customer exists for current store
            var customerId = await _genericAttributeService.GetAttributeAsync<string>(customer, SquarePaymentDefaults.CustomerIdAttribute);
            var squareCustomer = await _squarePaymentManager.GetCustomerAsync(customerId, storeId);
            if (squareCustomer == null)
                throw new NopException("Failed to retrieve customer");

            //set 'card on file'
            return paymentRequestBuilder
                .CustomerId(squareCustomer.Id)
                .SourceId(storedCardId.ToString())
                .Build()
                .ToExtendedRequest(integrationId);
        }

        //or try to get the card nonce
        var cardNonceKey = await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.CardNonce.Key");
        if (!paymentRequest.CustomValues.TryGetValue(cardNonceKey, out var cardNonce) || string.IsNullOrEmpty(cardNonce?.ToString()))
            throw new NopException("Failed to get the card nonce");

        //remove the card nonce from payment custom values, since it's no longer needed
        paymentRequest.CustomValues.Remove(cardNonceKey);

        //whether to save card details for the future purchasing
        var saveCardKey = await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.SaveCard.Key");
        var isGuest = await _customerService.IsGuestAsync(customer);
        if (paymentRequest.CustomValues.TryGetValue(saveCardKey, out var saveCardValue) && saveCardValue is bool saveCard && saveCard && !isGuest)
        {
            //remove the value from payment custom values, since it is no longer needed
            paymentRequest.CustomValues.Remove(saveCardKey);

            try
            {
                //check whether customer exists for current store
                var customerId = await _genericAttributeService.GetAttributeAsync<string>(customer, SquarePaymentDefaults.CustomerIdAttribute);
                var squareCustomer = await _squarePaymentManager.GetCustomerAsync(customerId, storeId);

                if (squareCustomer == null)
                {
                    //try to create the new one for current store, if not exists
                    var customerRequestBuilder = new SquareModel.CreateCustomerRequest.Builder()
                        .EmailAddress(customer.Email)
                        .Nickname(CommonHelper.EnsureMaximumLength(customer.Username, 100))
                        .GivenName(CommonHelper.EnsureMaximumLength(customer.FirstName, 300))
                        .FamilyName(CommonHelper.EnsureMaximumLength(customer.LastName, 300))
                        .PhoneNumber(customer.Phone)
                        .CompanyName(CommonHelper.EnsureMaximumLength(customer.Company, 500))
                        .ReferenceId(customer.CustomerGuid.ToString());

                    squareCustomer = await _squarePaymentManager.CreateCustomerAsync(customerRequestBuilder.Build(), storeId);
                    if (squareCustomer == null)
                        throw new NopException("Failed to create customer. Error details in the log");

                    //save customer identifier as generic attribute
                    await _genericAttributeService.SaveAttributeAsync(customer, SquarePaymentDefaults.CustomerIdAttribute, squareCustomer.Id);
                }

                //create request parameters to create the new card
                var cardRequestBuilder = new SquareModel.Card.Builder()
                    .CustomerId(squareCustomer.Id);

                var cardBillingAddress = billingAddress ?? shippingAddress;

                //set postal code
                var postalCodeKey = await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.PostalCode.Key");
                if (paymentRequest.CustomValues.TryGetValue(postalCodeKey, out var postalCode) && !string.IsNullOrEmpty(postalCode.ToString()))
                {
                    //remove the value from payment custom values, since it is no longer needed
                    paymentRequest.CustomValues.Remove(postalCodeKey);

                    cardBillingAddress ??= new SquareModel.Address();
                    cardBillingAddress = cardBillingAddress
                        .ToBuilder()
                        .PostalCode(CommonHelper.EnsureMaximumLength(postalCode.ToString(), 12))
                        .Build();
                }

                cardRequestBuilder.BillingAddress(cardBillingAddress);

                //try to create card for current store
                var createCardRequestBuilder = new SquareModel.CreateCardRequest.Builder(
                        idempotencyKey: Guid.NewGuid().ToString(),
                        sourceId: cardNonce.ToString(),
                        card: cardRequestBuilder.Build())
                    .VerificationToken(token?.ToString());
                var card = await _squarePaymentManager.CreateCustomerCardAsync(createCardRequestBuilder.Build(), storeId);
                if (card == null)
                    throw new NopException("Failed to create card. Error details in the log");

                //save card identifier to payment custom values for further purchasing
                if (isRecurringPayment)
                    paymentRequest.CustomValues.Add(storedCardKey, card.Id);

                //set 'card on file'
                return paymentRequestBuilder
                    .CustomerId(squareCustomer.Id)
                    .SourceId(card.Id)
                    .Build()
                    .ToExtendedRequest(integrationId);
            }
            catch (Exception exception)
            {
                await _logger.WarningAsync(exception.Message, exception, customer);
                if (isRecurringPayment)
                    throw new NopException("For recurring payments you need to save the card details");
            }
        }
        else if (isRecurringPayment)
            throw new NopException("For recurring payments you need to save the card details");

        //set 'card nonce'
        return paymentRequestBuilder
            .SourceId(cardNonce.ToString())
            .Build()
            .ToExtendedRequest(integrationId);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Process a payment
    /// </summary>
    /// <param name="processPaymentRequest">Payment info required for an order processing</param>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the process payment result
    /// </returns>
    public async Task<ProcessPaymentResult> ProcessPaymentAsync(ProcessPaymentRequest processPaymentRequest)
    {
        if (processPaymentRequest == null)
            throw new ArgumentException(nameof(processPaymentRequest));

        return await ProcessPaymentAsync(processPaymentRequest, false);
    }

    /// <summary>
    /// Post process payment (used by payment gateways that require redirecting to a third-party URL)
    /// </summary>
    /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public Task PostProcessPaymentAsync(PostProcessPaymentRequest postProcessPaymentRequest)
    {
        //do nothing
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns a value indicating whether payment method should be hidden during checkout
    /// </summary>
    /// <param name="cart">Shopping cart</param>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the rue - hide; false - display.
    /// </returns>
    public Task<bool> HidePaymentMethodAsync(IList<ShoppingCartItem> cart)
    {
        return Task.FromResult(false);
    }

    /// <summary>
    /// Gets additional handling fee
    /// </summary>
    /// <param name="cart">Shopping cart</param>
    /// <returns>The asynchronous task whose result contains the Additional handling fee</returns>
    public async Task<decimal> GetAdditionalHandlingFeeAsync(IList<ShoppingCartItem> cart)
    {
        return await _orderTotalCalculationService.CalculatePaymentAdditionalFeeAsync(cart,
            _squarePaymentSettings.AdditionalFee, _squarePaymentSettings.AdditionalFeePercentage);
    }

    /// <summary>
    /// Captures payment
    /// </summary>
    /// <param name="capturePaymentRequest">Capture payment request</param>
    /// <returns>The asynchronous task whose result contains the Capture payment result</returns>
    public async Task<CapturePaymentResult> CaptureAsync(CapturePaymentRequest capturePaymentRequest)
    {
        if (capturePaymentRequest == null)
            throw new ArgumentException(nameof(capturePaymentRequest));

        //capture transaction for current store
        var storeId = (await _storeContext.GetCurrentStoreAsync()).Id;
        var transactionId = capturePaymentRequest.Order.AuthorizationTransactionId;
        var completePaymentRequest = new SquareModel.CompletePaymentRequest();
        var (successfullyCompleted, error) = await _squarePaymentManager.CompletePaymentAsync(transactionId, completePaymentRequest, storeId);
        if (!successfullyCompleted)
            throw new NopException(error);

        //successfully captured
        return new CapturePaymentResult
        {
            NewPaymentStatus = PaymentStatus.Paid,
            CaptureTransactionId = transactionId
        };
    }

    /// <summary>
    /// Refunds a payment
    /// </summary>
    /// <param name="refundPaymentRequest">Request</param>
    /// <returns>The asynchronous task whose result contains the Refund payment result</returns>
    public async Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest refundPaymentRequest)
    {
        if (refundPaymentRequest == null)
            throw new ArgumentException(nameof(refundPaymentRequest));

        //get the primary store currency
        var currency = await _currencyService.GetCurrencyByIdAsync(_currencySettings.PrimaryStoreCurrencyId);
        if (currency == null)
            throw new NopException("Primary store currency cannot be loaded");

        //whether the currency is supported by the Square
        if (!CheckSupportCurrency(currency))
            throw new NopException($"The {currency.CurrencyCode} currency is not supported by the Square");

        //the amount of money in the smallest denomination of the currency indicated by currency. For example, when currency is USD, amount is in cents;
        //most currencies consist of 100 units of smaller denomination, so we multiply the total by 100
        var orderTotal = (int)(refundPaymentRequest.AmountToRefund * 100);
        var amountMoney = new SquareModel.Money(orderTotal, currency.CurrencyCode);

        //first try to get the transaction for current store
        var storeId = (await _storeContext.GetCurrentStoreAsync()).Id;
        var transactionId = refundPaymentRequest.Order.CaptureTransactionId;

        var paymentRefundRequest = new SquareModel.RefundPaymentRequest
        (
            idempotencyKey: Guid.NewGuid().ToString(),
            amountMoney: amountMoney,
            paymentId: transactionId
        );

        var (paymentRefund, paymentRefundError) = await _squarePaymentManager.RefundPaymentAsync(paymentRefundRequest, storeId);
        if (paymentRefund == null)
            throw new NopException(paymentRefundError);

        //if refund status is 'pending', try to refund once more with the same request parameters for current store
        if (paymentRefund.Status == SquarePaymentDefaults.Status.REFUND_PENDING)
        {
            (paymentRefund, paymentRefundError) = await _squarePaymentManager.RefundPaymentAsync(paymentRefundRequest, storeId);
            if (paymentRefund == null)
                throw new NopException(paymentRefundError);
        }

        //check whether refund is completed
        if (paymentRefund.Status != SquarePaymentDefaults.Status.REFUND_COMPLETED)
        {
            //change error notification to warning one (for the pending status)
            if (paymentRefund.Status == SquarePaymentDefaults.Status.REFUND_PENDING)
                _nopHtmlHelper.AddCssFileParts("~/Plugins/Payments.Square/Content/styles.css", null);

            return new RefundPaymentResult { Errors = new[] { $"Refund is {paymentRefund.Status}" }.ToList() };
        }

        //successfully refunded
        return new RefundPaymentResult
        {
            NewPaymentStatus = refundPaymentRequest.IsPartialRefund ? PaymentStatus.PartiallyRefunded : PaymentStatus.Refunded
        };
    }

    /// <summary>
    /// Voids a payment
    /// </summary>
    /// <param name="voidPaymentRequest">Request</param>
    /// <returns>The asynchronous task whose result contains the Void payment result</returns>
    public async Task<VoidPaymentResult> VoidAsync(VoidPaymentRequest voidPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(voidPaymentRequest);

        //void transaction for current store
        var storeId = (await _storeContext.GetCurrentStoreAsync()).Id;
        var transactionId = voidPaymentRequest.Order.AuthorizationTransactionId;
        var (successfullyCanceled, error) = await _squarePaymentManager.CancelPaymentAsync(transactionId, storeId);
        if (!successfullyCanceled)
            throw new NopException(error);

        //successfully voided
        return new VoidPaymentResult
        {
            NewPaymentStatus = PaymentStatus.Voided
        };
    }

    /// <summary>
    /// Process recurring payment
    /// </summary>
    /// <param name="processPaymentRequest">Payment info required for an order processing</param>
    /// <returns>The asynchronous task whose result contains the Process payment result</returns>
    public async Task<ProcessPaymentResult> ProcessRecurringPaymentAsync(ProcessPaymentRequest processPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(processPaymentRequest);

        return await ProcessPaymentAsync(processPaymentRequest, true);
    }

    /// <summary>
    /// Cancels a recurring payment
    /// </summary>
    /// <param name="cancelPaymentRequest">Request</param>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the result
    /// </returns>
    public Task<CancelRecurringPaymentResult> CancelRecurringPaymentAsync(CancelRecurringPaymentRequest cancelPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(cancelPaymentRequest);

        //always success
        return Task.FromResult(new CancelRecurringPaymentResult());
    }

    /// <summary>
    /// Gets a value indicating whether customers can complete a payment after order is placed but not completed (for redirection payment methods)
    /// </summary>
    /// <param name="order">Order</param>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the result
    /// </returns>
    public Task<bool> CanRePostProcessPaymentAsync(Order order)
    {
        return Task.FromResult(false);
    }

    /// <summary>
    /// Validate payment form
    /// </summary>
    /// <param name="form">The parsed form values</param>
    /// <returns>The asynchronous task whose result contains the List of validating errors</returns>
    public Task<IList<string>> ValidatePaymentFormAsync(IFormCollection form)
    {
        //try to get errors
        if (form.TryGetValue(nameof(PaymentInfoModel.Errors), out var errorsString) && !StringValues.IsNullOrEmpty(errorsString))
            return Task.FromResult<IList<string>>(errorsString.ToString().Split(separator, StringSplitOptions.RemoveEmptyEntries).ToList());

        return Task.FromResult<IList<string>>(new List<string>());
    }

    /// <summary>
    /// Get payment information
    /// </summary>
    /// <param name="form">The parsed form values</param>
    /// <returns>The asynchronous task whose result contains the Payment info holder</returns>
    public async Task<ProcessPaymentRequest> GetPaymentInfoAsync(IFormCollection form)
    {
        var paymentRequest = new ProcessPaymentRequest();

        //pass custom values to payment processor
        if (form.TryGetValue(nameof(PaymentInfoModel.Token), out var token) && !StringValues.IsNullOrEmpty(token))
            paymentRequest.CustomValues.Add(await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.Token.Key"), token.ToString());

        if (form.TryGetValue(nameof(PaymentInfoModel.CardNonce), out var cardNonce) && !StringValues.IsNullOrEmpty(cardNonce))
            paymentRequest.CustomValues.Add(await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.CardNonce.Key"), cardNonce.ToString());

        if (form.TryGetValue(nameof(PaymentInfoModel.StoredCardId), out var storedCardId) && !StringValues.IsNullOrEmpty(storedCardId) && !storedCardId.Equals(Guid.Empty.ToString()))
            paymentRequest.CustomValues.Add(await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.StoredCard.Key"), storedCardId.ToString());

        if (form.TryGetValue(nameof(PaymentInfoModel.SaveCard), out var saveCardValue) && !StringValues.IsNullOrEmpty(saveCardValue) && bool.TryParse(saveCardValue[0], out var saveCard) && saveCard)
            paymentRequest.CustomValues.Add(await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.SaveCard.Key"), saveCard);

        if (form.TryGetValue(nameof(PaymentInfoModel.PostalCode), out var postalCode) && !StringValues.IsNullOrEmpty(postalCode))
            paymentRequest.CustomValues.Add(await _localizationService.GetResourceAsync("Plugins.Payments.Square.Fields.PostalCode.Key"), postalCode.ToString());

        return paymentRequest;
    }

    /// <summary>
    /// Gets a configuration page URL
    /// </summary>
    public override string GetConfigurationPageUrl()
    {
        return $"{_webHelper.GetStoreLocation()}Admin/PaymentSquare/Configure";
    }

    /// <summary>
    /// Gets a type of a view component for displaying plugin in public store ("payment info" checkout step)
    /// </summary>
    /// <returns>View component type</returns>
    public Type GetPublicViewComponent()
    {
        return typeof(PaymentSquareViewComponent);
    }

    /// <summary>
    /// Install the plugin
    /// </summary>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override async Task InstallAsync()
    {
        //settings
        await _settingService.SaveSettingAsync(new SquarePaymentSettings
        {
            LocationId = "0",
            TransactionMode = TransactionMode.Charge,
            UseSandbox = true
        });

        //install renew access token schedule task
        if (await _scheduleTaskService.GetTaskByTypeAsync(SquarePaymentDefaults.RenewAccessTokenTask.Type) == null)
        {
            await _scheduleTaskService.InsertTaskAsync(new ScheduleTask
            {
                Enabled = true,
                Seconds = SquarePaymentDefaults.RenewAccessTokenTask.Period * 24 * 60 * 60,
                Name = SquarePaymentDefaults.RenewAccessTokenTask.Name,
                Type = SquarePaymentDefaults.RenewAccessTokenTask.Type,
                LastEnabledUtc = DateTime.UtcNow
            });
        }

        //locales
        await _localizationService.AddOrUpdateLocaleResourceAsync(new Dictionary<string, string>
        {
            ["Enums.Nop.Plugin.Payments.Square.Domain.TransactionMode.Authorize"] = "Authorize only",
            ["Enums.Nop.Plugin.Payments.Square.Domain.TransactionMode.Charge"] = "Charge (authorize and capture)",
            ["Plugins.Payments.Square.AccessTokenRenewalPeriod.Error"] = "Token renewal limit to {0} days max, but it is recommended that you specify {1} days for the period",
            ["Plugins.Payments.Square.Fields.AccessToken"] = "Access token",
            ["Plugins.Payments.Square.Fields.AccessToken.Hint"] = "Get the automatically renewed OAuth access token by pressing button 'Obtain access token'.",
            ["Plugins.Payments.Square.Fields.AdditionalFee"] = "Additional fee",
            ["Plugins.Payments.Square.Fields.AdditionalFee.Hint"] = "Enter additional fee to charge your customers.",
            ["Plugins.Payments.Square.Fields.AdditionalFeePercentage"] = "Additional fee. Use percentage",
            ["Plugins.Payments.Square.Fields.AdditionalFeePercentage.Hint"] = "Determines whether to apply a percentage additional fee to the order total. If not enabled, a fixed value is used.",
            ["Plugins.Payments.Square.Fields.ApplicationId"] = "Application ID",
            ["Plugins.Payments.Square.Fields.ApplicationId.Hint"] = "Enter your application ID, available from the application dashboard.",
            ["Plugins.Payments.Square.Fields.ApplicationSecret"] = "Application secret",
            ["Plugins.Payments.Square.Fields.ApplicationSecret.Hint"] = "Enter your application secret, available from the application dashboard.",
            ["Plugins.Payments.Square.Fields.CardNonce.Key"] = "Pay using card nonce",
            ["Plugins.Payments.Square.Fields.Location"] = "Business location",
            ["Plugins.Payments.Square.Fields.Location.Hint"] = "Choose your business location. Location is a required parameter for payment requests.",
            ["Plugins.Payments.Square.Fields.Location.NotExist"] = "No locations",
            ["Plugins.Payments.Square.Fields.Location.Select"] = "Select location",
            ["Plugins.Payments.Square.Fields.PostalCode"] = "Postal code",
            ["Plugins.Payments.Square.Fields.PostalCode.Key"] = "Postal code",
            ["Plugins.Payments.Square.Fields.SandboxAccessToken"] = "Sandbox access token",
            ["Plugins.Payments.Square.Fields.SandboxAccessToken.Hint"] = "Enter your sandbox access token, available from the application dashboard.",
            ["Plugins.Payments.Square.Fields.SandboxApplicationId"] = "Sandbox application ID",
            ["Plugins.Payments.Square.Fields.SandboxApplicationId.Hint"] = "Enter your sandbox application ID, available from the application dashboard.",
            ["Plugins.Payments.Square.Fields.SaveCard"] = "Save the card data for future purchasing",
            ["Plugins.Payments.Square.Fields.SaveCard.Key"] = "Save card details",
            ["Plugins.Payments.Square.Fields.StoredCard"] = "Use a previously saved card",
            ["Plugins.Payments.Square.Fields.StoredCard.Key"] = "Pay using stored card token",
            ["Plugins.Payments.Square.Fields.StoredCard.Mask"] = "*{0}",
            ["Plugins.Payments.Square.Fields.StoredCard.SelectCard"] = "Select a card",
            ["Plugins.Payments.Square.Fields.Token.Key"] = "Verification token",
            ["Plugins.Payments.Square.Fields.TransactionMode"] = "Transaction mode",
            ["Plugins.Payments.Square.Fields.TransactionMode.Hint"] = "Choose the transaction mode.",
            ["Plugins.Payments.Square.Fields.Use3ds"] = "Use 3D-Secure",
            ["Plugins.Payments.Square.Fields.Use3ds.Hint"] = "Determine whether to use 3D-Secure feature. Used for Strong customer authentication (SCA). SCA is generally friction-free for the buyer, but a card-issuing bank may require additional authentication for some payments. In those cases, the buyer must verify their identiy with the bank using an additional secure dialog.",
            ["Plugins.Payments.Square.Fields.UseSandbox"] = "Use sandbox",
            ["Plugins.Payments.Square.Fields.UseSandbox.Hint"] = "Determine whether to use sandbox credentials.",
            ["Plugins.Payments.Square.ObtainAccessToken"] = "Obtain access token",
            ["Plugins.Payments.Square.ObtainAccessToken.Error"] = "An error occurred while obtaining an access token",
            ["Plugins.Payments.Square.ObtainAccessToken.Success"] = "The access token was successfully obtained",
            ["Plugins.Payments.Square.PaymentMethodDescription"] = "Pay by credit card using Square",
            ["Plugins.Payments.Square.RenewAccessToken.Error"] = "Square payment error. An error occurred while renewing an access token",
            ["Plugins.Payments.Square.RenewAccessToken.Success"] = "Square payment info. The access token was successfully renewed",
            ["Plugins.Payments.Square.RevokeAccessTokens"] = "Revoke access tokens",
            ["Plugins.Payments.Square.RevokeAccessTokens.Error"] = "An error occurred while revoking access tokens",
            ["Plugins.Payments.Square.RevokeAccessTokens.Success"] = "All access tokens were successfully revoked"
        });

        await base.InstallAsync();
    }

    /// <summary>
    /// Uninstall the plugin
    /// </summary>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override async Task UninstallAsync()
    {
        //settings
        await _settingService.DeleteSettingAsync<SquarePaymentSettings>();

        //remove scheduled task
        var task = await _scheduleTaskService.GetTaskByTypeAsync(SquarePaymentDefaults.RenewAccessTokenTask.Type);
        if (task != null)
            await _scheduleTaskService.DeleteTaskAsync(task);

        //locales
        await _localizationService.DeleteLocaleResourcesAsync("Enums.Nop.Plugin.Payments.Square");
        await _localizationService.DeleteLocaleResourcesAsync("Plugins.Payments.Square");

        await base.UninstallAsync();
    }

    /// <summary>
    /// Gets a payment method description that will be displayed on checkout pages in the public store
    /// </summary>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task<string> GetPaymentMethodDescriptionAsync()
    {
        return await _localizationService.GetResourceAsync("Plugins.Payments.Square.PaymentMethodDescription");
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets a value indicating whether capture is supported
    /// </summary>
    public bool SupportCapture => true;

    /// <summary>
    /// Gets a value indicating whether partial refund is supported
    /// </summary>
    public bool SupportPartiallyRefund => true;

    /// <summary>
    /// Gets a value indicating whether refund is supported
    /// </summary>
    public bool SupportRefund => true;

    /// <summary>
    /// Gets a value indicating whether void is supported
    /// </summary>
    public bool SupportVoid => true;

    /// <summary>
    /// Gets a recurring payment type of payment method
    /// </summary>
    public RecurringPaymentType RecurringPaymentType => RecurringPaymentType.Manual;

    /// <summary>
    /// Gets a payment method type
    /// </summary>
    public PaymentMethodType PaymentMethodType => PaymentMethodType.Standard;

    /// <summary>
    /// Gets a value indicating whether we should display a payment information page for this plugin
    /// </summary>
    public bool SkipPaymentInfo => false;

    private static readonly char[] separator = new[] { '|' };

    #endregion
}