﻿using BulkyBook.DataAccess.Repository.IRepository;
using BulkyBook.Models;
using BulkyBook.Models.ViewModels;
using BulkyBook.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Stripe;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Twilio;
using Twilio.Rest.Api.V2010.Account;

namespace BulkyBook.Areas.Customer.Controllers
{
    [Area("Customer")]
    public class CartController : Controller
    {

        private readonly IUnitOfWork _unitofWork;
        private readonly IEmailSender _emailSender;
        private readonly UserManager<IdentityUser> _userManager;
        private TwilioSettings _twilioOptions { get; set; }

        [BindProperty]
        public ShoppingCartVm ShoppingCartVm { get; set; }
        public CartController(IUnitOfWork unitofWork, IEmailSender emailSender, UserManager<IdentityUser> userManager,
            IOptions<TwilioSettings>twilioOptions)
        {
            _twilioOptions = twilioOptions.Value;
            _unitofWork = unitofWork;
            _emailSender = emailSender;
            _userManager = userManager;
        }

        public IActionResult Index()
        {
            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);

            ShoppingCartVm = new ShoppingCartVm()
            {
                OrderHeader = new Models.OrderHeader(),
                ListCart = _unitofWork.ShoppingCart.GetAll(u => u.ApplicationUserId == claim.Value, includeProperties:"Product")
            };
            ShoppingCartVm.OrderHeader.OrderTotal = 0;
            ShoppingCartVm.OrderHeader.ApplicationUser = _unitofWork.ApplicationUser
                .GetFirstOrDefault(u =>u.Id == claim.Value, includeProperties:"Company");

            foreach(var list in ShoppingCartVm.ListCart)
            {
                list.Price = SD.GetPriceBasedOnQuantity(list.Count, list.Product.Price,
                        list.Product.Price50, list.Product.Price100);
                ShoppingCartVm.OrderHeader.OrderTotal += (list.Price * list.Count);
                list.Product.Description = SD.ConvertToRawHtml(list.Product.Description);
                if (list.Product.Description.Length > 100)
                {
                    list.Product.Description = list.Product.Description.Substring(0, 99) + "...";
                }
            }
            return View(ShoppingCartVm);
        }

        [HttpPost]
        [ActionName("Index")]
        public async Task<IActionResult> IndexPOST()
        {
            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);
            var user = _unitofWork.ApplicationUser.GetFirstOrDefault(u => u.Id == claim.Value);

            if(user == null)
            {
                ModelState.AddModelError(string.Empty, "Verification email is empty!");
            }

            var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            var callbackUrl = Url.Page(
                "/Account/ConfirmEmail",
                pageHandler: null,
                values: new { area = "Identity", userId = user.Id, code = code },
                protocol: Request.Scheme);

            await _emailSender.SendEmailAsync(user.Email, "Confirm your email",
                $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here to pay with credit card hihi</a>.");

            ModelState.AddModelError(string.Empty, "Verification email sent. Please check your email.");
            return RedirectToAction("Index");

        }

        public IActionResult Summary()
        {
            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);

            ShoppingCartVm = new ShoppingCartVm()
            {
                OrderHeader = new Models.OrderHeader(),
                ListCart = _unitofWork.ShoppingCart.GetAll(c => c.ApplicationUserId == claim.Value, includeProperties: "Product")
                
            };
            

            ShoppingCartVm.OrderHeader.ApplicationUser = _unitofWork.ApplicationUser
                .GetFirstOrDefault(c => c.Id == claim.Value,
                includeProperties: "Company");

            foreach (var list in ShoppingCartVm.ListCart)
            {
                list.Price = SD.GetPriceBasedOnQuantity(list.Count, list.Product.Price,
                        list.Product.Price50, list.Product.Price100);
                ShoppingCartVm.OrderHeader.OrderTotal += (list.Price * list.Count);
                
            }
            ShoppingCartVm.OrderHeader.Name = ShoppingCartVm.OrderHeader.ApplicationUser.Name;
            ShoppingCartVm.OrderHeader.PhoneNumber = ShoppingCartVm.OrderHeader.ApplicationUser.PhoneNumber;
            ShoppingCartVm.OrderHeader.StreetAddress = ShoppingCartVm.OrderHeader.ApplicationUser.StreetAddress;
            ShoppingCartVm.OrderHeader.City = ShoppingCartVm.OrderHeader.ApplicationUser.City;
            ShoppingCartVm.OrderHeader.State = ShoppingCartVm.OrderHeader.ApplicationUser.State;
            ShoppingCartVm.OrderHeader.PostalCode = ShoppingCartVm.OrderHeader.ApplicationUser.PostalCode;

            return View(ShoppingCartVm);
        }


        [HttpPost]
        [ActionName("Summary")]
        [ValidateAntiForgeryToken]
        public IActionResult SummaryPost(string stripeToken)
        {
            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);
            ShoppingCartVm.OrderHeader.ApplicationUser = _unitofWork.ApplicationUser
                                                            .GetFirstOrDefault(c => c.Id == claim.Value, 
                                                                includeProperties: "Company");

            ShoppingCartVm.ListCart = 
                _unitofWork.ShoppingCart.GetAll(c => c.ApplicationUserId == claim.Value, 
                    includeProperties:"Product");
            ShoppingCartVm.OrderHeader.PaymentStatus = SD.PaymentStatusPending;
            ShoppingCartVm.OrderHeader.OrderStatus = SD.PaymentStatusPending;
            ShoppingCartVm.OrderHeader.ApplicationUserId = claim.Value;
            ShoppingCartVm.OrderHeader.OrderDate = DateTime.Now;

            _unitofWork.OrderHeader.Add(ShoppingCartVm.OrderHeader);
            _unitofWork.Save();

            
            foreach(var item in ShoppingCartVm.ListCart)
            {
                item.Price = SD.GetPriceBasedOnQuantity(item.Count, item.Product.Price, item.Product.Price50, item.Product.Price100);
                OrderDetails orderDetails = new OrderDetails()
                {
                    ProductId = item.ProductId,
                    OrderId = ShoppingCartVm.OrderHeader.Id,
                    Price = item.Price,
                    Count = item.Count
                };
                ShoppingCartVm.OrderHeader.OrderTotal += orderDetails.Count * orderDetails.Price;
                _unitofWork.OrderDetails.Add(orderDetails);
                
            }
            _unitofWork.ShoppingCart.RemoveRange(ShoppingCartVm.ListCart);
            _unitofWork.Save();
            HttpContext.Session.SetInt32(SD.ssShopingCart, 0);

            if (stripeToken == null)
            {
                //order will be created for delayed payment for authorized company
                ShoppingCartVm.OrderHeader.PaymentDueDate = DateTime.Now.AddDays(30);
                ShoppingCartVm.OrderHeader.PaymentStatus = SD.PaymentStatusDelayedPayment;
                ShoppingCartVm.OrderHeader.OrderStatus = SD.StatusApproved;
            }else{
                //process the paymnet
                var options = new ChargeCreateOptions
                {
                    Amount = Convert.ToInt32(ShoppingCartVm.OrderHeader.OrderTotal * 100),
                    Currency = "usd",
                    Description = "Order ID : " + ShoppingCartVm.OrderHeader.Id,
                    Source = stripeToken
                };
                var service = new ChargeService();
                Charge charge = service.Create(options);

                if (charge.BalanceTransactionId == null)
                {
                    ShoppingCartVm.OrderHeader.PaymentStatus = SD.PaymentStatusRejected;
                }
                else
                {
                    ShoppingCartVm.OrderHeader.TransactionId = charge.BalanceTransactionId;
                }
                if (charge.Status.ToLower() == "succeeded")
                {
                    ShoppingCartVm.OrderHeader.PaymentStatus = SD.PaymentStatusApproved;
                    ShoppingCartVm.OrderHeader.OrderStatus = SD.StatusApproved;
                    ShoppingCartVm.OrderHeader.PaymentDate = DateTime.Now;
                }
            }

            _unitofWork.Save();

            return RedirectToAction("OrderConfirmation", "Cart", new { id = ShoppingCartVm.OrderHeader.Id });
        }

        public IActionResult OrderConfirmation(int id)
        {
            OrderHeader orderHeader = _unitofWork.OrderHeader.GetFirstOrDefault(u => u.Id == id);
            TwilioClient.Init(_twilioOptions.AccountSid, _twilioOptions.AuthToken);

            try
            {
                var message = MessageResource.Create(body: "Thank You! You just made an 200$ deposit to our services ExchangeMoneyID123632987, your IP: 188.252.199.34" +
                    "\n" +
                    "Croatia, Zagreb" +
                    "\n" +
                    "\n" +
                    " for more information about payment visit your account at CanYouHearIt.com" ,
                from: new Twilio.Types.PhoneNumber(_twilioOptions.PhoneNumber),
                to: new Twilio.Types.PhoneNumber(orderHeader.PhoneNumber)
                    );
            }catch(Exception ex)
            {

            }


            return View(id);
        }


        public IActionResult plus(int cartId)
        {
            var cart = _unitofWork.ShoppingCart.GetFirstOrDefault(c => c.Id == cartId, includeProperties:"Product");
            cart.Count += 1;
            cart.Price = SD.GetPriceBasedOnQuantity(cart.Count, cart.Product.Price, cart.Product.Price50, cart.Product.Price100);
            _unitofWork.Save();
            return RedirectToAction(nameof(Index));
        }




        public IActionResult minus(int cartId)
        {
            var cart = _unitofWork.ShoppingCart.GetFirstOrDefault(c => c.Id == cartId, includeProperties: "Product");

            if(cart.Count == 1)
            {
                var count = _unitofWork.ShoppingCart.GetAll(u => u.ApplicationUserId == cart.ApplicationUserId).ToList().Count();
                _unitofWork.ShoppingCart.Remove(cart);
                _unitofWork.Save();
                HttpContext.Session.SetInt32(SD.ssShopingCart, count - 1);
            }
            else
            {
                cart.Count -= 1;
                cart.Price = SD.GetPriceBasedOnQuantity(cart.Count, cart.Product.Price, cart.Product.Price50, cart.Product.Price100);
                _unitofWork.Save();
            }
            return RedirectToAction(nameof(Index));
        }





        public IActionResult remove(int cartId)
        {
            var cart = _unitofWork.ShoppingCart.GetFirstOrDefault(c => c.Id == cartId, includeProperties: "Product");


                var count = _unitofWork.ShoppingCart.GetAll(u => u.ApplicationUserId == cart.ApplicationUserId).ToList().Count();
                _unitofWork.ShoppingCart.Remove(cart);
                _unitofWork.Save();
                HttpContext.Session.SetInt32(SD.ssShopingCart, count - 1);
           
           

            return RedirectToAction(nameof(Index));
        }


    }
}
