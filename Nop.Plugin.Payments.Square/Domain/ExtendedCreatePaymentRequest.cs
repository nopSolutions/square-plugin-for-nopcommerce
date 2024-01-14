using System.Runtime.Serialization;
using Square.Models;

namespace Nop.Plugin.Payments.Square.Domain;

/// <summary>
/// Represents the parameters (with additional one) that can be included in the body of a request to the charge endpoint.
/// </summary>
public class ExtendedCreatePaymentRequest : CreatePaymentRequest
{
    #region Ctor

    public ExtendedCreatePaymentRequest(
        string sourceId,
        string idempotencyKey,
        Money amountMoney,
        Money tipMoney = null,
        Money appFeeMoney = null,
        string integrationId = null,
        string delayDuration = null,
        string delayAction = null,
        bool? autocomplete = null,
        string orderId = null,
        string customerId = null,
        string locationId = null,
        string teamMemberId = null,
        string referenceId = null,
        string verificationToken = null,
        bool? acceptPartialAuthorization = null,
        string buyerEmailAddress = null,
        Address billingAddress = null,
        Address shippingAddress = null,
        string note = null,
        string statementDescriptionIdentifier = null,
        CashPaymentDetails cashPaymentDetails = null,
        ExternalPaymentDetails externalPaymentDetails = null)
        : base(
            sourceId,
            idempotencyKey,
            amountMoney,
            tipMoney,
            appFeeMoney,
            delayDuration,
            delayAction,
            autocomplete,
            orderId,
            customerId,
            locationId,
            teamMemberId,
            referenceId,
            verificationToken,
            acceptPartialAuthorization,
            buyerEmailAddress,
            billingAddress,
            shippingAddress,
            note,
            statementDescriptionIdentifier,
            cashPaymentDetails,
            externalPaymentDetails)
    {
        IntegrationId = integrationId;
    }

    #endregion

    #region Properties

    /// <summary>
    /// An extra parameter in the request in order to track revenue share transaction attribution from 3rd party merchant/developer hosted apps (where the application id will not be known up front).
    /// </summary>
    /// <value>An extra parameter in the request in order to track revenue share transaction attribution from 3rd party merchant/developer hosted apps (where the application id will not be known up front).</value>
    [DataMember(Name = "integration_id", EmitDefaultValue = false)]
    public string IntegrationId { get; set; }

    #endregion
}