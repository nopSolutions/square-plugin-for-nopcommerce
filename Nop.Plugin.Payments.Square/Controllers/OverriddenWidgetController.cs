using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using Nop.Admin.Controllers;
using Nop.Admin.Extensions;
using Nop.Admin.Models.Cms;
using Nop.Core.Domain.Cms;
using Nop.Core.Plugins;
using Nop.Services.Cms;
using Nop.Services.Configuration;
using Nop.Services.Security;
using Nop.Web.Framework.Kendoui;

namespace Nop.Plugin.Payments.Square.Controllers
{
    public class OverriddenWidgetController : WidgetController
    {
        #region Fields

        private readonly IPermissionService _permissionService;
        private readonly IWidgetService _widgetService;
        private readonly WidgetSettings _widgetSettings;

        #endregion

        #region Ctor

        public OverriddenWidgetController(IWidgetService widgetService,
            IPermissionService permissionService,
            ISettingService settingService,
            WidgetSettings widgetSettings,
            IPluginFinder pluginFinder) : base(widgetService,
                permissionService,
                settingService,
                widgetSettings,
                pluginFinder)
        {
            this._permissionService = permissionService;
            this._widgetService = widgetService;
            this._widgetSettings = widgetSettings;
        }

        #endregion

        #region Methods
        
        [HttpPost]
        public override ActionResult List(DataSourceRequest command)
        {
            if (!_permissionService.Authorize(StandardPermissionProvider.ManageWidgets))
                return AccessDeniedKendoGridJson();

            //exclude Square plugin from the widget list
            var widgets = _widgetService.LoadAllWidgets()
                .Where(widget => !widget.PluginDescriptor.SystemName.Equals(SquarePaymentDefaults.SystemName));
            
            var widgetsModel = new List<WidgetModel>();
            foreach (var widget in widgets)
            {
                var tmp1 = widget.ToModel();
                tmp1.IsActive = widget.IsWidgetActive(_widgetSettings);
                widgetsModel.Add(tmp1);
            }
            widgetsModel = widgetsModel.ToList();
            var gridModel = new DataSourceResult
            {
                Data = widgetsModel,
                Total = widgetsModel.Count()
            };

            return Json(gridModel);
        }

        #endregion
    }
}