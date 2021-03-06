﻿using BTCPayServer.Models;
using BTCPayServer.Models.ServerViewModels;
using BTCPayServer.Services;
using BTCPayServer.Services.Mails;
using BTCPayServer.Services.Rates;
using BTCPayServer.Validations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Threading.Tasks;

namespace BTCPayServer.Controllers
{
    [Authorize(Roles = Roles.ServerAdmin)]
    public class ServerController : Controller
    {
        private UserManager<ApplicationUser> _UserManager;
        SettingsRepository _SettingsRepository;
        private IRateProviderFactory _RateProviderFactory;

        public ServerController(UserManager<ApplicationUser> userManager,
            IRateProviderFactory rateProviderFactory,
            SettingsRepository settingsRepository)
        {
            _UserManager = userManager;
            _SettingsRepository = settingsRepository;
            _RateProviderFactory = rateProviderFactory;
        }

        [Route("server/rates")]
        public async Task<IActionResult> Rates()
        {
            var rates = (await _SettingsRepository.GetSettingAsync<RatesSetting>()) ?? new RatesSetting();
            return View(new RatesViewModel()
            {
                CacheMinutes = rates.CacheInMinutes,
                PrivateKey = rates.PrivateKey,
                PublicKey = rates.PublicKey
            });

        }

        class TestCoinAverageAuthenticator : ICoinAverageAuthenticator
        {
            private RatesSetting settings;

            public TestCoinAverageAuthenticator(RatesSetting settings)
            {
                this.settings = settings;
            }
            public Task AddHeader(HttpRequestMessage message)
            {
                var sig = settings.GetCoinAverageSignature();
                if (sig != null)
                    message.Headers.Add("X-signature", settings.GetCoinAverageSignature());
                return Task.CompletedTask;
            }
        }
        [Route("server/rates")]
        [HttpPost]
        public async Task<IActionResult> Rates(RatesViewModel vm)
        {
            var rates = (await _SettingsRepository.GetSettingAsync<RatesSetting>()) ?? new RatesSetting();
            rates.PrivateKey = vm.PrivateKey;
            rates.PublicKey = vm.PublicKey;
            rates.CacheInMinutes = vm.CacheMinutes;
            try
            {
                if (rates.GetCoinAverageSignature() != null)
                {
                    await new CoinAverageRateProvider("BTC")
                    { Authenticator = new TestCoinAverageAuthenticator(rates) }.TestAuthAsync();
                }
            }
            catch
            {
                ModelState.AddModelError(nameof(vm.PrivateKey), "Invalid API key pair");
            }
            if (!ModelState.IsValid)
                return View(vm);
            await _SettingsRepository.UpdateSetting(rates);
            ((BTCPayRateProviderFactory)_RateProviderFactory).CacheSpan = TimeSpan.FromMinutes(vm.CacheMinutes);
            StatusMessage = "Rate settings successfully updated";
            return RedirectToAction(nameof(Rates));
        }

        [Route("server/users")]
        public IActionResult ListUsers()
        {
            var users = new UsersViewModel();
            users.StatusMessage = StatusMessage;
            users.Users
                = _UserManager.Users.Select(u => new UsersViewModel.UserViewModel()
                {
                    Name = u.UserName,
                    Email = u.Email,
                    Id = u.Id
                }).ToList();
            return View(users);
        }

        [Route("server/users/{userId}")]
        public new async Task<IActionResult> User(string userId)
        {
            var user = await _UserManager.FindByIdAsync(userId);
            if (user == null)
                return NotFound();
            var roles = await _UserManager.GetRolesAsync(user);
            var userVM = new UserViewModel();
            userVM.Id = user.Id;
            userVM.IsAdmin = IsAdmin(roles);
            return View(userVM);
        }

        private static bool IsAdmin(IList<string> roles)
        {
            return roles.Contains(Roles.ServerAdmin, StringComparer.Ordinal);
        }

        [Route("server/users/{userId}")]
        [HttpPost]
        public new async Task<IActionResult> User(string userId, UserViewModel viewModel)
        {
            var user = await _UserManager.FindByIdAsync(userId);
            if (user == null)
                return NotFound();
            var roles = await _UserManager.GetRolesAsync(user);
            var isAdmin = IsAdmin(roles);
            bool updated = false;

            if (isAdmin != viewModel.IsAdmin)
            {
                if (viewModel.IsAdmin)
                    await _UserManager.AddToRoleAsync(user, Roles.ServerAdmin);
                else
                    await _UserManager.RemoveFromRoleAsync(user, Roles.ServerAdmin);
                updated = true;
            }
            if (updated)
            {
                viewModel.StatusMessage = "User successfully updated";
            }
            return View(viewModel);
        }


        [Route("server/users/{userId}/delete")]
        public async Task<IActionResult> DeleteUser(string userId)
        {
            var user = userId == null ? null : await _UserManager.FindByIdAsync(userId);
            if (user == null)
                return NotFound();
            return View("Confirm", new ConfirmModel()
            {
                Title = "Delete user " + user.Email,
                Description = "This user will be permanently deleted",
                Action = "Delete"
            });
        }

        [Route("server/users/{userId}/delete")]
        [HttpPost]
        public async Task<IActionResult> DeleteUserPost(string userId)
        {
            var user = userId == null ? null : await _UserManager.FindByIdAsync(userId);
            if (user == null)
                return NotFound();
            await _UserManager.DeleteAsync(user);
            StatusMessage = "User deleted";
            return RedirectToAction(nameof(ListUsers));
        }

        [TempData]
        public string StatusMessage
        {
            get; set;
        }

        [Route("server/emails")]
        public async Task<IActionResult> Emails()
        {
            var data = (await _SettingsRepository.GetSettingAsync<EmailSettings>()) ?? new EmailSettings();
            return View(new EmailsViewModel() { Settings = data });
        }

        [Route("server/policies")]
        public async Task<IActionResult> Policies()
        {
            var data = (await _SettingsRepository.GetSettingAsync<PoliciesSettings>()) ?? new PoliciesSettings();
            return View(data);
        }
        [Route("server/policies")]
        [HttpPost]
        public async Task<IActionResult> Policies(PoliciesSettings settings)
        {
            await _SettingsRepository.UpdateSetting(settings);
            TempData["StatusMessage"] = "Policies upadated successfully";
            return View(settings);
        }

        [Route("server/emails")]
        [HttpPost]
        public async Task<IActionResult> Emails(EmailsViewModel model, string command)
        {
            if (command == "Test")
            {
                if (!ModelState.IsValid)
                    return View(model);
                try
                {
                    var client = model.Settings.CreateSmtpClient();
                    await client.SendMailAsync(model.Settings.From, model.TestEmail, "BTCPay test", "BTCPay test");
                    model.StatusMessage = "Email sent to " + model.TestEmail + ", please, verify you received it";
                }
                catch (Exception ex)
                {
                    model.StatusMessage = "Error: " + ex.Message;
                }
                return View(model);
            }
            else
            {
                ModelState.Remove(nameof(model.TestEmail));
                if (!ModelState.IsValid)
                    return View(model);
                await _SettingsRepository.UpdateSetting(model.Settings);
                model.StatusMessage = "Email settings saved";
                return View(model);
            }
        }
    }
}
