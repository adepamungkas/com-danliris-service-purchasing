﻿using AutoMapper;
using Com.DanLiris.Service.Purchasing.Lib.Models.GarmentDispositionPurchaseModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.GarmentExternalPurchaseOrderModel;
using Com.DanLiris.Service.Purchasing.Lib.Services;
using Com.DanLiris.Service.Purchasing.Lib.ViewModels.GarmentDispositionPurchase;
using Com.Moonlay.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Com.DanLiris.Service.Purchasing.Lib.Facades.GarmentDispositionPurchaseFacades
{
    public class GarmentDispositionPurchaseFacade : IGarmentDispositionPurchaseFacade
    {
        private string USER_AGENT = "Facade";

        private readonly PurchasingDbContext dbContext;
        private readonly DbSet<GarmentDispositionPurchase> dbSet;
        public readonly IServiceProvider serviceProvider;
        private readonly IdentityService identityService;
        private readonly IMapper mapper;


        public GarmentDispositionPurchaseFacade(PurchasingDbContext dbContext, IServiceProvider serviceProvider, IdentityService identityService, IMapper mapper)
        {
            this.dbContext = dbContext;
            this.dbSet = dbContext.GarmentDispositionPurchases;
            this.serviceProvider = serviceProvider;
            this.identityService = (IdentityService)serviceProvider.GetService(typeof(IdentityService));
            this.mapper = mapper;
        }

        public async Task<int> Post(FormDto model)
        {
            int Created = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    GarmentDispositionPurchase dataModel = mapper.Map<FormDto, GarmentDispositionPurchase>(model);
                    EntityExtension.FlagForCreate(dataModel, identityService.Username, USER_AGENT);
                    var DispositionNo = await GenerateNo(identityService.TimezoneOffset);
                    dataModel.DispositionNo = DispositionNo;
                    dataModel.GarmentDispositionPurchaseItems.ForEach(s=> {
                        EntityExtension.FlagForCreate(s, identityService.Username, USER_AGENT);
                        s.IsDispositionCreated = true;
                        s.GarmentDispositionPurchaseDetails.ForEach(t =>
                        {
                            if(t.QTYPaid>= t.QTYRemains)
                            {
                                var EPOItems = this.dbContext.GarmentExternalPurchaseOrderItems.Where(a => a.Id == t.EPO_POId).FirstOrDefault();
                                EPOItems.IsDispositionCreatedAll = true;
                                EntityExtension.FlagForUpdate(EPOItems, identityService.Username, USER_AGENT);
                                var afterUpdateModel = this.dbContext.GarmentExternalPurchaseOrderItems.Update(EPOItems);
                            }
                            EntityExtension.FlagForCreate(t, identityService.Username, USER_AGENT);
                        });
                    });

                    var afterAddModel = dbContext.GarmentDispositionPurchases.Add(dataModel);
                    Created = await dbContext.SaveChangesAsync();

                    transaction.Commit();
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    throw new Exception(e.Message);
                }
            }

            return Created;
        }

        async Task<string> GenerateNo(int clientTimeZoneOffset)
        {
            DateTimeOffset Now = DateTime.UtcNow;
            string Year = Now.ToOffset(new TimeSpan(clientTimeZoneOffset, 0, 0)).ToString("yy");
            string Month = Now.ToOffset(new TimeSpan(clientTimeZoneOffset, 0, 0)).ToString("MM");

            string no = $"{Year}-{Month}-GJ" ;
            int Padding = 3;

            var lastNo = await this.dbSet.Where(w => w.DispositionNo.StartsWith(no) && !w.IsDeleted).OrderByDescending(o => o.DispositionNo).FirstOrDefaultAsync();
            no = $"{no}";

            if (lastNo == null)
            {
                return no + "1".PadLeft(Padding, '0');
            }
            else
            {
                int lastNoNumber = Int32.Parse(lastNo.DispositionNo.Replace(no, "")) + 1;
                return no + lastNoNumber.ToString().PadLeft(Padding, '0');
            }
        }

        public async Task<int> Delete(int id)
        {
            int Deleted = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    var dataModel = dbSet.FirstOrDefault(s => s.Id == id);

                    EntityExtension.FlagForDelete(dataModel, identityService.Username, USER_AGENT);

                    var afterDeletedModel = dbContext.GarmentDispositionPurchases.Update(dataModel);
                    Deleted = await dbContext.SaveChangesAsync();
                    transaction.Commit();
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    throw new Exception(e.Message);
                }
            }

            return Deleted;
        }

        public async Task<FormDto> GetFormById(int id)
        {
            var dataModel = await dbSet
                .AsNoTracking()
                    .Include(p => p.GarmentDispositionPurchaseItems)
                        .ThenInclude(p => p.GarmentDispositionPurchaseDetails)
                .Where(d => d.Id.Equals(id))
                .FirstOrDefaultAsync();

            var model = mapper.Map<GarmentDispositionPurchase, FormDto>(dataModel);
            return model;
        }

        public async Task<DispositionPurchaseIndexDto> GetAll(string keyword, int page, int size)
        {
            var dataModel = dbSet
                .AsNoTracking()
                    .Include(p => p.GarmentDispositionPurchaseItems)
                        .ThenInclude(p => p.GarmentDispositionPurchaseDetails).AsQueryable();

            if (keyword != null)
                dataModel = dataModel.Where(s => s.DispositionNo.Contains(keyword) || s.SupplierName.Contains(keyword));

            var countData = dataModel.Count();

            var dataList = await dataModel.ToListAsync();

            var model = mapper.Map<List<GarmentDispositionPurchase>, List<DispositionPurchaseTableDto>>(dataList);

            var indexModel = new DispositionPurchaseIndexDto(model, page, countData);
            return indexModel;
        }

        public async Task<List<FormDto>> ReadByDispositionNo(string dispositionNo, int page, int size)
        {
            var dataModel = dbSet
                .AsNoTracking()
                    .Include(p => p.GarmentDispositionPurchaseItems)
                        .ThenInclude(p => p.GarmentDispositionPurchaseDetails).AsQueryable();

            if (dispositionNo != null)
                dataModel = dataModel.Where(s => s.DispositionNo.Contains(dispositionNo));


            var dataList = await dataModel.OrderBy(s => s.DispositionNo).ToListAsync();

            var model = mapper.Map<List<GarmentDispositionPurchase>, List<FormDto>>(dataList);
            return model;
        }

        public async Task<int> Update(FormDto model)
        {
            int Updated = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    //validation
                    var dataExist = dbSet.FirstOrDefault(s => s.Id == model.Id);
                    if (dataExist == null)
                    {
                        throw new Exception("Data Not Found");
                    }
                    GarmentDispositionPurchase dataModel = mapper.Map<FormDto, GarmentDispositionPurchase>(model);
                    EntityExtension.FlagForUpdate(dataModel, identityService.Username, USER_AGENT);

                    var afterUpdateModel = dbContext.GarmentDispositionPurchases.Update(dataModel);
                    Updated = await dbContext.SaveChangesAsync();
                    transaction.Commit();
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    throw new Exception(e.Message);
                }
            }

            return Updated;
        }

        public GarmentExternalPurchaseOrderViewModel ReadByEPOWithDisposition(int EPOid,int supplierId, int currencyId)
        {
            //var EPObyId = this.dbContext.GarmentExternalPurchaseOrders.Where(p => p.Id == EPOid && p.SupplierId == supplierId && p.CurrencyId == currencyId)
            var EPObyId = this.dbContext.GarmentExternalPurchaseOrders.Where(p => p.Id == EPOid)
                .Include(p => p.Items)
                .FirstOrDefault();
            EPObyId.Items = EPObyId.Items.Where(s => s.IsDispositionCreatedAll == false).ToList();
            //var POIds = EPObyId.SelectMany(s=> s.Items).Select(s => (long)s.POId).ToList();
            var POIds = EPObyId.Items.Select(s => (long)s.POId).ToList();


            var IPOByEPO = this.dbContext.GarmentInternalPurchaseOrders.Where(s => POIds.Contains(s.Id)).ToList();
            //var IPOUnits = IPOByEPO.Select(s => new { s.UnitId, s.UnitCode, s.UnitName });

            GarmentExternalPurchaseOrderViewModel viewModel = mapper.Map<GarmentExternalPurchaseOrderViewModel>(EPObyId);

            //get disposition
            var searchDisposition = this.dbContext.GarmentDispositionPurchases
                .Include(s => s.GarmentDispositionPurchaseItems)
                .ThenInclude(s=> s.GarmentDispositionPurchaseDetails)
                .Where(s => s.GarmentDispositionPurchaseItems.Any(t => t.EPOId == EPOid)
                && s.SupplierId == supplierId
                && s.CurrencyId == currencyId
                );

            var dispositionPaid = searchDisposition.SelectMany(s=> s.GarmentDispositionPurchaseItems).Where(s => s.IsDispositionPaid);
            var dispositionCreated = searchDisposition.SelectMany(s=> s.GarmentDispositionPurchaseItems).Where(s => s.IsDispositionCreated);
            //viewModel.ForEach(Model => { 
            viewModel.DispositionAmountCreated = dispositionCreated.SelectMany(s=> s.GarmentDispositionPurchaseDetails).Sum(t => t.PaidPrice);
            viewModel.DispositionAmountPaid = dispositionPaid.SelectMany(s => s.GarmentDispositionPurchaseDetails).Sum(t => t.PaidPrice);
            viewModel.DispositionQuantityCreated = dispositionCreated
                .SelectMany(t => t.GarmentDispositionPurchaseDetails).Sum(t => t.QTYPaid);
            viewModel.DispositionQuantityPaid = dispositionPaid
                .SelectMany(t => t.GarmentDispositionPurchaseDetails).Sum(t => t.QTYPaid);
            //foreach Unit

            viewModel.Items.ForEach(t =>
            {
                var getIPO = IPOByEPO.Where(s => s.Id == t.POId).FirstOrDefault();
                t.UnitId = getIPO.UnitId;
                t.UnitName = getIPO.UnitName;
                t.UnitCode = getIPO.UnitCode;

                var searchDispositionIPO = this.dbContext.GarmentDispositionPurchases
                .Include(s => s.GarmentDispositionPurchaseItems)
                .ThenInclude(s => s.GarmentDispositionPurchaseDetails)
                .Where(s => s.GarmentDispositionPurchaseItems.Any(j => j.GarmentDispositionPurchaseDetails.Any(d=> d.IPOId == t.POId))
                && s.SupplierId == supplierId
                && s.CurrencyId == currencyId
                );

                var dispositionPaidIPO = searchDispositionIPO.SelectMany(d => d.GarmentDispositionPurchaseItems).Where(d => d.IsDispositionPaid);
                var dispositionCreatedIPO = searchDispositionIPO.SelectMany(d => d.GarmentDispositionPurchaseItems).Where(d => d.IsDispositionCreated);
                t.DispositionAmountCreated = dispositionCreatedIPO.SelectMany(d => d.GarmentDispositionPurchaseDetails).Where(d=> d.IPOId == t.POId).Sum(j => j.PaidPrice);
                t.DispositionAmountPaid = dispositionPaidIPO.SelectMany(d => d.GarmentDispositionPurchaseDetails).Where(d => d.IPOId == t.POId).Sum(j => j.PaidPrice);
                t.DispositionQuantityCreated = dispositionCreatedIPO
                    .SelectMany(j => j.GarmentDispositionPurchaseDetails).Where(d => d.IPOId == t.POId).Sum(j => j.QTYPaid);
                t.DispositionQuantityPaid = dispositionPaidIPO
                    .SelectMany(j => j.GarmentDispositionPurchaseDetails).Where(d => d.IPOId == t.POId).Sum(j => j.QTYPaid);

            });
            //});

            return viewModel;
        }
    }
}
