﻿using Aguacongas.IdentityServer.Store;
using Aguacongas.TheIdServer.BlazorApp;
using Aguacongas.TheIdServer.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RichardSzalay.MockHttp;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Aguacongas.TheIdServer.IntegrationTest.BlazorApp.Pages
{
    [Collection("api collection")]
    public class RoleTest : EntityPageTestBase
    {
        public override string Entity => "role";

        public RoleTest(ApiFixture fixture, ITestOutputHelper testOutputHelper):base(fixture, testOutputHelper)
        {
        }

        [Fact]
        public async Task OnFilterChanged_should_filter_claims()
        {
            string roleId = await CreateRole();

            CreateTestHost("Alice Smith",
                SharedConstants.WRITER,
                roleId,
                out TestHost host,
                out RenderedComponent<App> component,
                out MockHttpMessageHandler mockHttp);

            WaitForLoaded(host, component);

            WaitForContains(host, component, "filtered");

            var filterInput = component.Find("input[placeholder=\"filter\"]");

            Assert.NotNull(filterInput);

            host.WaitForNextRender(async () => await filterInput.TriggerEventAsync("oninput", new ChangeEventArgs
            {
                Value = roleId
            }));

            var markup = component.GetMarkup();

            Assert.DoesNotContain("filtered", markup);
        }

        [Fact]
        public async Task SaveClick_should_create_role()
        {
            CreateTestHost("Alice Smith",
                SharedConstants.WRITER,
                null,
                out TestHost host,
                out RenderedComponent<App> component,
                out MockHttpMessageHandler mockHttp);

            WaitForLoaded(host, component);

            var input = WaitForNode(host, component, "#name");

            var roleName = GenerateId();

            host.WaitForNextRender(() => input.Change(roleName));
            
            var form = component.Find("form");
            Assert.NotNull(form);

            host.WaitForNextRender(() => form.Submit());

            WaitForSavedToast(host, component);

            await DbActionAsync<ApplicationDbContext>(async context =>
            {
                var role = await context.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
                Assert.NotNull(role);
            });
        }


        [Fact]
        public async Task DeleteButtonClick_should_delete_Role()
        {
            string roleId = await CreateRole();

            CreateTestHost("Alice Smith",
                SharedConstants.WRITER,
                roleId,
                out TestHost host,
                out RenderedComponent<App> component,
                out MockHttpMessageHandler mockHttp);

            WaitForLoaded(host, component);

            var input = WaitForNode(host, component, "#delete-entity input");

            host.WaitForNextRender(() => input.Change(roleId));

            var confirm = component.Find("#delete-entity button.btn-danger");

            host.WaitForNextRender(() => confirm.Click());

            WaitForDeletedToast(host, component);

            await DbActionAsync<ApplicationDbContext>(async context =>
            {
                var role = await context.Roles.FirstOrDefaultAsync(a => a.Id == roleId);
                Assert.Null(role);
            });
        }

        [Fact]
        public async Task AddRoleClaim_should_add_claim_to_role()
        {
            string roleId = await CreateRole();

            CreateTestHost("Alice Smith",
                SharedConstants.WRITER,
                roleId,
                out TestHost host,
                out RenderedComponent<App> component,
                out MockHttpMessageHandler mockHttp);

            WaitForLoaded(host, component);

            var addButton = WaitForNode(host, component, "#claims button");

            host.WaitForNextRender(() => addButton.Click());

            var rows = component.FindAll("#claims tr");

            Assert.NotNull(rows);

            var lastRow = rows.Last();
            var inputList = lastRow.Descendants("input");

            Assert.NotEmpty(inputList);

            var expected = GenerateId();
            host.WaitForNextRender(() => inputList.First().Change(expected));

            rows = component.FindAll("#claims tr");
            lastRow = rows.Last();
            inputList = lastRow.Descendants("input");

            host.WaitForNextRender(() => inputList.Last().Change(expected));

            var form = component.Find("form");

            Assert.NotNull(form);

            host.WaitForNextRender(() => form.Submit());

            WaitForSavedToast(host, component);

            await DbActionAsync<ApplicationDbContext>(async context =>
            {
                var claim = await context.RoleClaims.FirstOrDefaultAsync(t => t.RoleId == roleId &&
                    t.ClaimType == expected &&
                    t.ClaimValue == expected);
                Assert.NotNull(claim);
            });
        }

        [Fact]
        public async Task UpdateRoleClaim_should_update_claim()
        {
            string roleId = await CreateRole();

            CreateTestHost("Alice Smith",
                SharedConstants.WRITER,
                roleId,
                out TestHost host,
                out RenderedComponent<App> component,
                out MockHttpMessageHandler mockHttp);

            WaitForLoaded(host, component);

            var rows = WaitForAllNodes(host, component, "#claims tr");

            var lastRow = rows.Last();
            var inputList = lastRow.Descendants("input");

            Assert.NotEmpty(inputList);

            var expected = GenerateId();

            host.WaitForNextRender(() => inputList.Last().Change(expected));

            var form = component.Find("form");

            Assert.NotNull(form);

            host.WaitForNextRender(() => form.Submit());

            WaitForSavedToast(host, component);

            await DbActionAsync<ApplicationDbContext>(async context =>
            {
                var claim = await context.RoleClaims.FirstOrDefaultAsync(t => t.RoleId == roleId &&
                    t.ClaimValue == expected);
                Assert.NotNull(claim);
            });
        }

        [Fact]
        public async Task DeleteRoleClaim_should_remove_claim_from_role()
        {
            string roleId = await CreateRole();

            CreateTestHost("Alice Smith",
                SharedConstants.WRITER,
                roleId,
                out TestHost host,
                out RenderedComponent<App> component,
                out MockHttpMessageHandler mockHttp);

            WaitForLoaded(host, component);

            var button = WaitForNode(host, component, "#claims tr button");

            host.WaitForNextRender(() => button.Click());

            var form = component.Find("form");

            Assert.NotNull(form);

            host.WaitForNextRender(() => form.Submit());

            WaitForSavedToast(host, component);

            await DbActionAsync<ApplicationDbContext>(async context =>
            {
                var claim = await context.RoleClaims.FirstOrDefaultAsync(t => t.RoleId == roleId);
                Assert.Null(claim);
            });
        }

        private async Task<string> CreateRole()
        {
            var roleId = GenerateId();
            await DbActionAsync<ApplicationDbContext>(context =>
            {
                context.Roles.Add(new IdentityRole
                {
                    Id = roleId,
                    Name = roleId
                });
                context.RoleClaims.Add(new IdentityRoleClaim<string>
                {
                    RoleId = roleId,
                    ClaimType = "filtered",
                    ClaimValue = "filtered"
                });
                return context.SaveChangesAsync();
            });
            return roleId;
        }
    }
}
