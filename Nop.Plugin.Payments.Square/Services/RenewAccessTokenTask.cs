using System;
using Nop.Core;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Payments;
using Nop.Services.ScheduleTasks;

namespace Nop.Plugin.Payments.Square.Services
{
    /// <summary>
    /// Represents a schedule task to renew the access token
    /// </summary>
    public class RenewAccessTokenTask : IScheduleTask
    {
        #region Fields

        private readonly ILocalizationService _localizationService;
        private readonly ILogger _logger;
        private readonly IPaymentPluginManager _paymentPluginManager;
        private readonly ISettingService _settingService;
        private readonly SquarePaymentManager _squarePaymentManager;
        private readonly SquarePaymentSettings _squarePaymentSettings;
        private readonly IStoreContext _storeContext;

        #endregion

        #region Ctor

        public RenewAccessTokenTask(ILocalizationService localizationService,
            ILogger logger,
            IPaymentPluginManager paymentPluginManager,
            ISettingService settingService,
            SquarePaymentManager squarePaymentManager,
            SquarePaymentSettings squarePaymentSettings,
            IStoreContext storeContext)
        {
            _localizationService = localizationService;
            _logger = logger;
            _paymentPluginManager = paymentPluginManager;
            _settingService = settingService;
            _squarePaymentManager = squarePaymentManager;
            _squarePaymentSettings = squarePaymentSettings;
            _storeContext = storeContext;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Executes a task
        /// </summary>
        public async System.Threading.Tasks.Task ExecuteAsync()
        {
            //whether plugin is active
            if (!await _paymentPluginManager.IsPluginActiveAsync(SquarePaymentDefaults.SystemName))
                return;

            //do not execute for sandbox environment
            if (_squarePaymentSettings.UseSandbox)
                return;

            try
            {
                var storeId = (await _storeContext.GetCurrentStoreAsync()).Id;

                //get the new access token
                var (newAccessToken, refreshToken) = await _squarePaymentManager.RenewAccessTokenAsync(storeId);
                if (string.IsNullOrEmpty(newAccessToken) || string.IsNullOrEmpty(refreshToken))
                    throw new NopException("No service response");

                //if access token successfully received, save it for the further usage
                _squarePaymentSettings.AccessToken = newAccessToken;
                _squarePaymentSettings.RefreshToken = refreshToken;

                await _settingService.SaveSettingAsync(_squarePaymentSettings, x => x.AccessToken, storeId, false);
                await _settingService.SaveSettingAsync(_squarePaymentSettings, x => x.RefreshToken, storeId, false);

                await _settingService.ClearCacheAsync();

                //log information about the successful renew of the access token
                await _logger.InformationAsync(await _localizationService.GetResourceAsync("Plugins.Payments.Square.RenewAccessToken.Success"));
            }
            catch (Exception exception)
            {
                //log error on renewing of the access token
                await _logger.ErrorAsync(await _localizationService.GetResourceAsync("Plugins.Payments.Square.RenewAccessToken.Error"), exception);
            }
        }

        #endregion
    }
}