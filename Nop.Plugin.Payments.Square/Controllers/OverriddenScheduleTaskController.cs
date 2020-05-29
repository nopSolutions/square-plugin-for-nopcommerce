using System.Web.Mvc;
using Nop.Admin.Controllers;
using Nop.Admin.Models.Tasks;
using Nop.Services.Helpers;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Payments;
using Nop.Services.Security;
using Nop.Services.Tasks;

namespace Nop.Plugin.Payments.Square.Controllers
{
    public partial class OverriddenScheduleTaskController : ScheduleTaskController
    {
		#region Fields

        private readonly ILocalizationService _localizationService;
        private readonly IPaymentService _paymentService;
        private readonly IScheduleTaskService _scheduleTaskService;

        #endregion

        #region Ctor

        public OverriddenScheduleTaskController(ICustomerActivityService customerActivityService,
            IDateTimeHelper dateTimeHelper,
            ILocalizationService localizationService, 
            IPaymentService paymentService,
            IPermissionService permissionService,
            IScheduleTaskService scheduleTaskService) : base(scheduleTaskService, 
                permissionService, 
                dateTimeHelper, 
                localizationService, 
                customerActivityService)
        {
            this._localizationService = localizationService;
            this._paymentService = paymentService;
            this._scheduleTaskService = scheduleTaskService;
        }

        #endregion

        #region Utilities

        private void CheckRenewAccessTokenTask(ScheduleTaskModel model)
        {
            //whether the updated task is a renew access token task
            var scheduleTask = _scheduleTaskService.GetTaskById(model.Id);
            if (!scheduleTask?.Type.Equals(SquarePaymentDefaults.RenewAccessTokenTask) ?? true)
                return;

            //check whether the plugin is installed
            if (!(_paymentService.LoadPaymentMethodBySystemName(SquarePaymentDefaults.SystemName)?.PluginDescriptor?.Installed ?? false))
                return;

            //check token renewal limit
            var accessTokenRenewalPeriod = model.Seconds / 60 / 60 / 24;
            if (accessTokenRenewalPeriod > SquarePaymentDefaults.AccessTokenRenewalPeriodMax)
            {
                var error = string.Format(_localizationService.GetResource("Plugins.Payments.Square.AccessTokenRenewalPeriod.Error"),
                    SquarePaymentDefaults.AccessTokenRenewalPeriodMax, SquarePaymentDefaults.AccessTokenRenewalPeriodRecommended);
                this.ModelState.AddModelError(string.Empty, error);
            }
        }

        #endregion

        #region Methods

        [HttpPost]
        public override ActionResult TaskUpdate(ScheduleTaskModel model)
        {
            //check token renewal limit for the renew access token task
            CheckRenewAccessTokenTask(model);

            return base.TaskUpdate(model);
        }
        
        #endregion
    }
}