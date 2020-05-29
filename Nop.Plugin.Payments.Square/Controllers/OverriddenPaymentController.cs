using System.Web.Mvc;
using Nop.Admin.Controllers;
using Nop.Admin.Models.Payments;
using Nop.Core;
using Nop.Core.Domain.Cms;
using Nop.Core.Domain.Payments;
using Nop.Core.Plugins;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using Nop.Services.Localization;
using Nop.Services.Payments;
using Nop.Services.Security;
using Nop.Web.Framework.Mvc;

namespace Nop.Plugin.Payments.Square.Controllers
{
    public partial class OverriddenPaymentController : PaymentController
	{
		#region Fields

        private readonly IPaymentService _paymentService;
        private readonly IPermissionService _permissionService;
        private readonly IPluginFinder _pluginFinder;
        private readonly ISettingService _settingService;
        private readonly PaymentSettings _paymentSettings;
        private readonly WidgetSettings _widgetSettings;

        #endregion

        #region Ctor

        public OverriddenPaymentController(ICountryService countryService,
            ILocalizationService localizationService,
            IPaymentService paymentService,
            IPermissionService permissionService,
            IPluginFinder pluginFinder,
            ISettingService settingService,             
            IWebHelper webHelper,
            PaymentSettings paymentSettings,
            WidgetSettings widgetSettings) : base(paymentService,
                paymentSettings,
                settingService,
                permissionService,
                countryService,
                pluginFinder,
                webHelper,
                localizationService)
		{
            this._paymentService = paymentService;
            this._permissionService = permissionService;
            this._pluginFinder = pluginFinder;
            this._settingService = settingService;
            this._paymentSettings = paymentSettings;
            this._widgetSettings = widgetSettings;
        }

		#endregion 

        #region Methods
        
        [HttpPost]
        public override ActionResult MethodUpdate([Bind(Exclude = "ConfigurationRouteValues")] PaymentMethodModel model)
        {
            if (!_permissionService.Authorize(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            var pm = _paymentService.LoadPaymentMethodBySystemName(model.SystemName);
            if (pm.IsPaymentMethodActive(_paymentSettings))
            {
                if (!model.IsActive)
                {
                    //mark as disabled
                    _paymentSettings.ActivePaymentMethodSystemNames.Remove(pm.PluginDescriptor.SystemName);
                    _settingService.SaveSetting(_paymentSettings);

                    //accordingly update widgets of Square plugin
                    if (model.SystemName.Equals(SquarePaymentDefaults.SystemName))
                    {
                        if (_widgetSettings.ActiveWidgetSystemNames.Contains(SquarePaymentDefaults.SystemName))
                            _widgetSettings.ActiveWidgetSystemNames.Remove(SquarePaymentDefaults.SystemName);
                        _settingService.SaveSetting(_widgetSettings);
                    }
                }
            }
            else
            {
                if (model.IsActive)
                {
                    //mark as active
                    _paymentSettings.ActivePaymentMethodSystemNames.Add(pm.PluginDescriptor.SystemName);
                    _settingService.SaveSetting(_paymentSettings);
                    
                    //accordingly update widgets of Square plugin
                    if (model.SystemName.Equals(SquarePaymentDefaults.SystemName))
                    {
                        if (!_widgetSettings.ActiveWidgetSystemNames.Contains(SquarePaymentDefaults.SystemName))
                            _widgetSettings.ActiveWidgetSystemNames.Add(SquarePaymentDefaults.SystemName);
                        _settingService.SaveSetting(_widgetSettings);
                    }
                }
            }

            var pluginDescriptor = pm.PluginDescriptor;
            pluginDescriptor.FriendlyName = model.FriendlyName;
            pluginDescriptor.DisplayOrder = model.DisplayOrder;
            PluginFileParser.SavePluginDescriptionFile(pluginDescriptor);
            
            //reset plugin cache
            _pluginFinder.ReloadPlugins();

            return new NullJsonResult();
        }
        
        #endregion
    }
}
