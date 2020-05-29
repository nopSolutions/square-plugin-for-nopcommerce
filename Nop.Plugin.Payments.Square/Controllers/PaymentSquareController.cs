using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using Nop.Core;
using Nop.Core.Domain.Customers;
using Nop.Plugin.Payments.Square.Domain;
using Nop.Plugin.Payments.Square.Models;
using Nop.Plugin.Payments.Square.Services;
using Nop.Services;
using Nop.Services.Common;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Payments;
using Nop.Services.Security;
using Nop.Web.Framework.Controllers;

namespace Nop.Plugin.Payments.Square.Controllers
{
    public class PaymentSquareController : BasePaymentController
    {
        #region Fields

        private readonly ILocalizationService _localizationService;
        private readonly IPermissionService _permissionService;
        private readonly ISettingService _settingService;
        private readonly IWorkContext _workContext;
        private readonly SquarePaymentManager _squarePaymentManager;
        private readonly SquarePaymentSettings _squarePaymentSettings;

        #endregion

        #region Ctor

        public PaymentSquareController(ILocalizationService localizationService,
            IPermissionService permissionService,
            ISettingService settingService,
            IWorkContext workContext,
            SquarePaymentManager squarePaymentManager,
            SquarePaymentSettings squarePaymentSettings)
        {
            this._localizationService = localizationService;
            this._permissionService = permissionService;
            this._settingService = settingService;
            this._workContext = workContext;
            this._squarePaymentManager = squarePaymentManager;
            this._squarePaymentSettings = squarePaymentSettings;
        }

        #endregion

        #region Methods

        [AdminAuthorize]
        [ChildActionOnly]
        public ActionResult Configure()
        {
            //prepare model
            var model = new ConfigurationModel
            {
                ApplicationId = _squarePaymentSettings.ApplicationId,
                ApplicationSecret = _squarePaymentSettings.ApplicationSecret,
                AccessToken = _squarePaymentSettings.AccessToken,
                UseSandbox = _squarePaymentSettings.UseSandbox,
                TransactionModeId = (int)_squarePaymentSettings.TransactionMode,
                TransactionModes = _squarePaymentSettings.TransactionMode.ToSelectList(),
                LocationId = _squarePaymentSettings.LocationId,
                AdditionalFee = _squarePaymentSettings.AdditionalFee,
                AdditionalFeePercentage = _squarePaymentSettings.AdditionalFeePercentage
            };

            //prepare business locations, every payment a merchant processes is associated with one of these locations
            if (!string.IsNullOrEmpty(model.AccessToken))
            {
                model.Locations = _squarePaymentManager.GetActiveLocations().Select(location =>
                {
                    var name = location.BusinessName;
                    if (!location.Name.Equals(location.BusinessName))
                        name = $"{name} ({location.Name})";
                    return new SelectListItem { Text = name, Value = location.Id };
                }).ToList();
            }

            //add the special item for 'there are no location' with value 0
            if (!model.Locations.Any())
            {
                var noLocationText = _localizationService.GetResource("Plugins.Payments.Square.Fields.Location.NotExist");
                model.Locations.Add(new SelectListItem { Text = noLocationText, Value = "0" });
            }

            return View("~/Plugins/Payments.Square/Views/Configure.cshtml", model);
        }

        [HttpPost, ActionName("Configure")]
        [FormValueRequired("save")]
        [AdminAuthorize]
        [ChildActionOnly]
        public ActionResult Configure(ConfigurationModel model)
        {
            if (!ModelState.IsValid)
                return Configure();

            //save settings
            _squarePaymentSettings.ApplicationId = model.ApplicationId;
            _squarePaymentSettings.ApplicationSecret = model.ApplicationSecret;
            _squarePaymentSettings.AccessToken = model.AccessToken;
            _squarePaymentSettings.UseSandbox = model.UseSandbox;
            _squarePaymentSettings.TransactionMode = (TransactionMode)model.TransactionModeId;
            _squarePaymentSettings.LocationId = model.LocationId;
            _squarePaymentSettings.AdditionalFee = model.AdditionalFee;
            _squarePaymentSettings.AdditionalFeePercentage = model.AdditionalFeePercentage;
            _settingService.SaveSetting(_squarePaymentSettings);

            //warn admin that the location is a required parameter
            if (string.IsNullOrEmpty(_squarePaymentSettings.LocationId) || _squarePaymentSettings.LocationId.Equals("0"))
                WarningNotification(_localizationService.GetResource("Plugins.Payments.Square.Fields.Location.Hint"));
            else
                SuccessNotification(_localizationService.GetResource("Admin.Plugins.Saved"));

            return Configure();
        }
        
        [HttpPost]
        public ActionResult ObtainAccessToken()
        {
            //create new verification string
            _squarePaymentSettings.AccessTokenVerificationString = Guid.NewGuid().ToString();
            _settingService.SaveSetting(_squarePaymentSettings);

            //get the URL to directs a Square merchant's web browser
            var redirectUrl = _squarePaymentManager.GenerateAuthorizeUrl(_squarePaymentSettings.AccessTokenVerificationString);

            return Json(new { url = redirectUrl });
        }

        [HttpPost, ActionName("Configure")]
        [FormValueRequired("revokeAccessTokens")]
        [AdminAuthorize]
        [ChildActionOnly]
        public ActionResult RevokeAccessTokens(ConfigurationModel model)
        {
            try
            {
                //try to revoke all access tokens
                var successfullyRevoked = _squarePaymentManager.RevokeAccessTokens(new RevokeAccessTokenRequest
                {
                    ApplicationId = _squarePaymentSettings.ApplicationId,
                    ApplicationSecret = _squarePaymentSettings.ApplicationSecret,
                    AccessToken = _squarePaymentSettings.AccessToken
                });
                if (!successfullyRevoked)
                    throw new NopException("Tokens were not revoked");

                //if access token successfully revoked, delete it from the settings
                _squarePaymentSettings.AccessToken = string.Empty;
                _settingService.SaveSetting(_squarePaymentSettings);

                SuccessNotification(_localizationService.GetResource("Plugins.Payments.Square.RevokeAccessTokens.Success"));
            }
            catch (Exception exception)
            {
                ErrorNotification(_localizationService.GetResource("Plugins.Payments.Square.RevokeAccessTokens.Error"));
                if (!string.IsNullOrEmpty(exception.Message))
                    ErrorNotification(exception.Message);
            }

            return Configure();
        }

        [ChildActionOnly]
        public ActionResult PaymentInfo()
        {
            var model = new PaymentInfoModel
            {

                //whether current customer is guest
                IsGuest = _workContext.CurrentCustomer.IsGuest(),

                //get postal code from the billing address or from the shipping one
                PostalCode = _workContext.CurrentCustomer.BillingAddress?.ZipPostalCode
                ?? _workContext.CurrentCustomer.ShippingAddress?.ZipPostalCode
            };

            //whether customer already has stored cards
            var customerId = _workContext.CurrentCustomer.GetAttribute<string>(SquarePaymentDefaults.CustomerIdAttribute);
            var customer = _squarePaymentManager.GetCustomer(customerId);
            if (customer?.Cards != null)
            {
                var cardNumberMask = _localizationService.GetResource("Plugins.Payments.Square.Fields.StoredCard.Mask");
                model.StoredCards = customer.Cards.Select(card => new SelectListItem { Text = string.Format(cardNumberMask, card.Last4), Value = card.Id }).ToList();
            }

            //add the special item for 'select card' with value 0
            if (model.StoredCards.Any())
            {
                var selectCardText = _localizationService.GetResource("Plugins.Payments.Square.Fields.StoredCard.SelectCard");
                model.StoredCards.Insert(0, new SelectListItem { Text = selectCardText, Value = "0" });
            }

            return View("~/Plugins/Payments.Square/Views/PaymentInfo.cshtml", model);
        }

        [NonAction]
        public override IList<string> ValidatePaymentForm(FormCollection form)
        {
            //try to get errors
            var errors = form["Errors"];
            if (!string.IsNullOrEmpty(errors))
                return errors.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            return new List<string>();
        }

        [NonAction]
        public override ProcessPaymentRequest GetPaymentInfo(FormCollection form)
        {
            var paymentRequest = new ProcessPaymentRequest();

            //pass custom values to payment processor
            var cardNonce = form["CardNonce"];
            if (!string.IsNullOrEmpty(cardNonce))
                paymentRequest.CustomValues.Add(_localizationService.GetResource("Plugins.Payments.Square.Fields.CardNonce.Key"), cardNonce);

            var storedCardId = form["StoredCardId"];
            if (!string.IsNullOrEmpty(storedCardId) && !storedCardId.Equals("0"))
                paymentRequest.CustomValues.Add(_localizationService.GetResource("Plugins.Payments.Square.Fields.StoredCard.Key"), storedCardId);

            var saveCardValue = form["SaveCard"];
            if (!string.IsNullOrEmpty(saveCardValue) && bool.TryParse(saveCardValue.Split(',').FirstOrDefault(), out bool saveCard) && saveCard)
                paymentRequest.CustomValues.Add(_localizationService.GetResource("Plugins.Payments.Square.Fields.SaveCard.Key"), saveCard);

            var postalCode = form["PostalCode"];
            if (!string.IsNullOrEmpty(postalCode))
                paymentRequest.CustomValues.Add(_localizationService.GetResource("Plugins.Payments.Square.Fields.PostalCode.Key"), postalCode);

            return paymentRequest;
        }

        [ChildActionOnly]
        public ActionResult OnePageCheckoutScript()
        {
            return View("~/Plugins/Payments.Square/Views/OnePageCheckoutScript.cshtml");
        }

        public ActionResult AccessTokenCallback()
        {
            //handle access token callback
            try
            {
                if (string.IsNullOrEmpty(_squarePaymentSettings.ApplicationId) || string.IsNullOrEmpty(_squarePaymentSettings.ApplicationSecret))
                    throw new NopException("Plugin is not configured");

                //check whether there are errors in the request
                var error = this.Request.QueryString["error"];
                var errorDescription = this.Request.QueryString["error_description"];
                if (!string.IsNullOrEmpty(error) || !string.IsNullOrEmpty(errorDescription))
                    throw new NopException($"{error} - {errorDescription}");

                //validate verification string
                var verificationString = this.Request.QueryString["state"];
                if (string.IsNullOrEmpty(verificationString) || !verificationString.Equals(_squarePaymentSettings.AccessTokenVerificationString))
                    throw new NopException("The verification string did not pass the validation");

                //check whether there is an authorization code in the request
                var authorizationCode = this.Request.QueryString["code"];
                if (string.IsNullOrEmpty(authorizationCode))
                    throw new NopException("No service response");

                //exchange the authorization code for an access token
                var accessToken = _squarePaymentManager.ObtainAccessToken(new ObtainAccessTokenRequest
                {
                    ApplicationId = _squarePaymentSettings.ApplicationId,
                    ApplicationSecret = _squarePaymentSettings.ApplicationSecret,
                    AuthorizationCode = authorizationCode,
                    RedirectUrl = this.Url.RouteUrl(SquarePaymentDefaults.AccessTokenRoute)
                });
                if (string.IsNullOrEmpty(accessToken))
                    throw new NopException("No service response");

                //if access token successfully received, save it for the further usage
                _squarePaymentSettings.AccessToken = accessToken;
                _settingService.SaveSetting(_squarePaymentSettings);

                SuccessNotification(_localizationService.GetResource("Plugins.Payments.Square.ObtainAccessToken.Success"));
            }
            catch (Exception exception)
            {
                //display errors
                ErrorNotification(_localizationService.GetResource("Plugins.Payments.Square.ObtainAccessToken.Error"));
                if (!string.IsNullOrEmpty(exception.Message))
                    ErrorNotification(exception.Message);
            }

            //we cannot redirect to the Configure action since it is only for child requests, so redirect to the Payment.ConfigureMethod
            return RedirectToAction("ConfigureMethod", "Payment", new { systemName = SquarePaymentDefaults.SystemName, area = "admin" });
        }

        #endregion
    }
}