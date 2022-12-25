using System.Threading.Tasks;
using Nop.Services.Events;
using Nop.Services.Localization;
using Nop.Services.Payments;
using Nop.Services.ScheduleTasks;
using Nop.Web.Areas.Admin.Models.Tasks;
using Nop.Web.Framework.Events;
using Nop.Web.Framework.Models;
using Nop.Web.Framework.UI;

namespace Nop.Plugin.Payments.Square.Services
{
    /// <summary>
    /// Represents plugin event consumer
    /// </summary>
    public class EventConsumer :
        IConsumer<PageRenderingEvent>,
        IConsumer<ModelReceivedEvent<BaseNopModel>>
    {
        #region Fields

        private readonly ILocalizationService _localizationService;
        private readonly IPaymentPluginManager _paymentPluginManager;
        private readonly IScheduleTaskService _scheduleTaskService;
        private readonly SquarePaymentSettings _squarePaymentSettings;

        #endregion

        #region Ctor

        public EventConsumer(ILocalizationService localizationService,
            IPaymentPluginManager paymentPluginManager,
            IScheduleTaskService scheduleTaskService,
            SquarePaymentSettings squarePaymentSettings)
        {
            _localizationService = localizationService;
            _paymentPluginManager = paymentPluginManager;
            _scheduleTaskService = scheduleTaskService;
            _squarePaymentSettings = squarePaymentSettings;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Handle event
        /// </summary>
        /// <param name="eventMessage">Event</param>
        /// <returns>A task that represents the asynchronous operation</returns>
        public async Task HandleEventAsync(PageRenderingEvent eventMessage)
        {
            //check whether the plugin is active
            if (!await _paymentPluginManager.IsPluginActiveAsync(SquarePaymentDefaults.SystemName))
                return;

            //add js script to one page checkout
            if (eventMessage.GetRouteName()?.Equals(SquarePaymentDefaults.OnePageCheckoutRouteName) ?? false)
            {
                eventMessage.Helper?.AddScriptParts(ResourceLocation.Footer,
                    _squarePaymentSettings.UseSandbox ? SquarePaymentDefaults.SandboxPaymentFormScriptPath : SquarePaymentDefaults.PaymentFormScriptPath,
                    excludeFromBundle: true);
            }
        }

        /// <summary>
        /// Handle event
        /// </summary>
        /// <param name="eventMessage">Event</param>
        /// <returns>A task that represents the asynchronous operation</returns>
        public async Task HandleEventAsync(ModelReceivedEvent<BaseNopModel> eventMessage)
        {
            //whether received model is ScheduleTaskModel
            if (eventMessage?.Model is not ScheduleTaskModel scheduleTaskModel)
                return;

            //whether renew access token task is changed
            var scheduleTask = await _scheduleTaskService.GetTaskByIdAsync(scheduleTaskModel.Id);
            if (!scheduleTask?.Type.Equals(SquarePaymentDefaults.RenewAccessTokenTask.Type) ?? true)
                return;

            //check token renewal limit
            var accessTokenRenewalPeriod = scheduleTaskModel.Seconds / 60 / 60 / 24;
            if (accessTokenRenewalPeriod > SquarePaymentDefaults.RenewAccessTokenTask.MaxPeriod)
            {
                var error = string.Format(await _localizationService.GetResourceAsync("Plugins.Payments.Square.AccessTokenRenewalPeriod.Error"),
                    SquarePaymentDefaults.RenewAccessTokenTask.MaxPeriod, SquarePaymentDefaults.RenewAccessTokenTask.Period);
                eventMessage.ModelState.AddModelError(string.Empty, error);
            }
        }

        #endregion
    }
}