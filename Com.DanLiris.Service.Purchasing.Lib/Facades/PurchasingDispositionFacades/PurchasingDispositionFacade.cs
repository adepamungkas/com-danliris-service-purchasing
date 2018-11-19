﻿using Com.DanLiris.Service.Purchasing.Lib.Helpers;
using Com.DanLiris.Service.Purchasing.Lib.Interfaces;
using Com.DanLiris.Service.Purchasing.Lib.Models.ExternalPurchaseOrderModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.PurchasingDispositionModel;
using Com.Moonlay.Models;
using Com.Moonlay.NetCore.Lib;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Com.DanLiris.Service.Purchasing.Lib.Facades.PurchasingDispositionFacades
{
    public class PurchasingDispositionFacade : IPurchasingDispositionFacade
    {
        private readonly PurchasingDbContext dbContext;
        public readonly IServiceProvider serviceProvider;
        private readonly DbSet<PurchasingDisposition> dbSet;

        public PurchasingDispositionFacade(IServiceProvider serviceProvider, PurchasingDbContext dbContext)
        {
            this.serviceProvider = serviceProvider;
            this.dbContext = dbContext;
            this.dbSet = dbContext.Set<PurchasingDisposition>();
        }

        public Tuple<List<PurchasingDisposition>, int, Dictionary<string, string>> Read(int Page = 1, int Size = 25, string Order = "{}", string Keyword = null, string Filter = "{}")
        {
            IQueryable<PurchasingDisposition> Query = this.dbSet;

            List<string> searchAttributes = new List<string>()
            {
                "DispositionNo","SupplierName","Items.EPONo","CurrencyCode"
            };

            Query = QueryHelper<PurchasingDisposition>.ConfigureSearch(Query, searchAttributes, Keyword);

            Query = Query
                .Select(s => new PurchasingDisposition
                {
                    DispositionNo = s.DispositionNo,
                    Id = s.Id,
                    SupplierCode = s.SupplierCode,
                    SupplierId = s.SupplierId,
                    SupplierName = s.SupplierName,
                    Bank = s.Bank,
                    CurrencyCode = s.CurrencyCode,
                    CurrencyId = s.CurrencyId,
                    CurrencyRate = s.CurrencyRate,
                    ConfirmationOrderNo = s.ConfirmationOrderNo,
                    InvoiceNo = s.InvoiceNo,
                    PaymentMethod = s.PaymentMethod,
                    PaymentDueDate=s.PaymentDueDate,
                    CreatedBy = s.CreatedBy,
                    LastModifiedUtc = s.LastModifiedUtc,
                    CreatedUtc=s.CreatedUtc,
                    Items = s.Items.ToList()
                });



            Dictionary<string, string> FilterDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Filter);
            Query = QueryHelper<PurchasingDisposition>.ConfigureFilter(Query, FilterDictionary);

            Dictionary<string, string> OrderDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Order);
            Query = QueryHelper<PurchasingDisposition>.ConfigureOrder(Query, OrderDictionary);

            Pageable<PurchasingDisposition> pageable = new Pageable<PurchasingDisposition>(Query, Page - 1, Size);
            List<PurchasingDisposition> Data = pageable.Data.ToList<PurchasingDisposition>();
            int TotalData = pageable.TotalCount;

            return Tuple.Create(Data, TotalData, OrderDictionary);
        }

        public PurchasingDisposition ReadModelById(int id)
        {
            var a = this.dbSet.Where(d => d.Id.Equals(id) && d.IsDeleted.Equals(false))
                .Include(p => p.Items)
                .ThenInclude(p => p.Details)
                .FirstOrDefault();
            return a;
        }

        public async Task<int> Create(PurchasingDisposition m, string user, int clientTimeZoneOffset)
        {
            int Created = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    EntityExtension.FlagForCreate(m, user, "Facade");
                    m.DispositionNo = await GenerateNo(m, clientTimeZoneOffset);

                    foreach (var item in m.Items)
                    {
                        EntityExtension.FlagForCreate(item, user, "Facade");
                        foreach (var detail in item.Details)
                        {
                            ExternalPurchaseOrderDetail epoDetail = this.dbContext.ExternalPurchaseOrderDetails.Where(s => s.Id.ToString() == detail.EPODetailId && s.IsDeleted == false).FirstOrDefault();
                            epoDetail.DispositionQuantity += detail.PaidQuantity;
                            EntityExtension.FlagForCreate(detail, user, "Facade");
                        }
                    }

                    this.dbSet.Add(m);
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

        async Task<string> GenerateNo(PurchasingDisposition model, int clientTimeZoneOffset)
        {
            DateTimeOffset Now = DateTime.UtcNow;
            string Year = Now.ToOffset(new TimeSpan(clientTimeZoneOffset, 0, 0)).ToString("yy"); ;
            string Month = Now.ToOffset(new TimeSpan(clientTimeZoneOffset, 0, 0)).ToString("MM"); ;

            string no = $"{Year}-{Month}-";
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

        public int Delete(int id, string user)
        {
            int Deleted = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    var m = this.dbSet
                        .Include(d => d.Items)
                        .ThenInclude(d => d.Details)
                        .SingleOrDefault(epo => epo.Id == id && !epo.IsDeleted);

                    EntityExtension.FlagForDelete(m, user, "Facade");

                    foreach (var item in m.Items)
                    {
                        EntityExtension.FlagForDelete(item, user, "Facade");
                        foreach (var detail in item.Details)
                        {
                            ExternalPurchaseOrderDetail epoDetail = this.dbContext.ExternalPurchaseOrderDetails.Where(s => s.Id.ToString() == detail.EPODetailId && s.IsDeleted == false).FirstOrDefault();
                            epoDetail.DispositionQuantity -= detail.PaidQuantity;
                            EntityExtension.FlagForDelete(detail, user, "Facade");
                        }
                    }

                    Deleted = dbContext.SaveChanges();
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

        public async Task<int> Update(int id, PurchasingDisposition purchasingDisposition, string user)
        {
            int Updated = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    var existingModel = this.dbSet.AsNoTracking()
                        .Include(d => d.Items)
                        .ThenInclude(d => d.Details)
                        .Single(epo => epo.Id == id && !epo.IsDeleted);

                    foreach(var oldIem in existingModel.Items)
                    {
                        foreach(var oldDetail in oldIem.Details)
                        {
                            ExternalPurchaseOrderDetail epoDetail = this.dbContext.ExternalPurchaseOrderDetails.Where(s => s.Id.ToString() == oldDetail.EPODetailId && s.IsDeleted == false).FirstOrDefault();
                            epoDetail.DispositionQuantity -= oldDetail.PaidQuantity;
                        }
                    }

                    if (existingModel != null && id == purchasingDisposition.Id)
                    {
                        EntityExtension.FlagForUpdate(purchasingDisposition, user, "Facade");

                        foreach (var item in purchasingDisposition.Items.ToList())
                        {
                            var existingItem = existingModel.Items.SingleOrDefault(m => m.Id == item.Id);
                            List<PurchasingDispositionItem> duplicateDispositionItems = purchasingDisposition.Items.Where(i => i.EPOId == item.EPOId && i.Id != item.Id).ToList();

                            if (item.Id == 0)
                            {
                                if (duplicateDispositionItems.Count <= 0)
                                {

                                    EntityExtension.FlagForCreate(item, user, "Facade");

                                    foreach (var detail in item.Details)
                                    {
                                        ExternalPurchaseOrderDetail epoDetail = this.dbContext.ExternalPurchaseOrderDetails.Where(s => s.Id.ToString() == detail.EPODetailId && s.IsDeleted == false).FirstOrDefault();
                                        epoDetail.DispositionQuantity += detail.PaidQuantity;
                                        EntityExtension.FlagForCreate(detail, user, "Facade");
                                    }
                                    
                                }
                            }
                            else
                            {
                                EntityExtension.FlagForUpdate(item, user, "Facade");

                                if (duplicateDispositionItems.Count > 0)
                                {
                                    //foreach (var detail in item.Details.ToList())
                                    //{
                                    //    if (detail.Id != 0)
                                    //    {

                                    //        EntityExtension.FlagForUpdate(detail, user, "Facade");

                                    //        foreach (var duplicateItem in duplicateDispositionItems.ToList())
                                    //        {
                                    //            foreach (var duplicateDetail in duplicateItem.Details.ToList())
                                    //            {
                                    //                if (item.Details.Count(d => d.EPODetailId.Equals(duplicateDetail.EPODetailId)) < 1)
                                    //                {
                                    //                    ExternalPurchaseOrderDetail epoDetail = this.dbContext.ExternalPurchaseOrderDetails.Where(s => s.Id == detail.EPODetailId && s.IsDeleted == false).FirstOrDefault();
                                    //                    epoDetail.DispositionQuantity += detail.PaidQuantity;
                                    //                    EntityExtension.FlagForCreate(duplicateDetail, user, "Facade");
                                    //                    item.Details.Add(duplicateDetail);
                                                        
                                    //                }
                                    //            }
                                    //            purchasingDisposition.Items.Remove(duplicateItem);
                                    //        }
                                    //    }
                                    //}
                                }
                                else
                                {
                                    foreach (var detail in item.Details)
                                    {
                                        if (detail.Id != 0)
                                        {
                                            ExternalPurchaseOrderDetail epoDetail = this.dbContext.ExternalPurchaseOrderDetails.Where(s => s.Id.ToString() == detail.EPODetailId && s.IsDeleted == false).FirstOrDefault();
                                            epoDetail.DispositionQuantity += detail.PaidQuantity;
                                            EntityExtension.FlagForUpdate(detail, user, "Facade");
                                        }
                                    }
                                }
                            }
                        }

                        this.dbContext.Update(purchasingDisposition);

                        foreach (var existingItem in existingModel.Items)
                        {
                            var newItem = purchasingDisposition.Items.FirstOrDefault(i => i.Id == existingItem.Id);
                            if (newItem == null)
                            {
                                EntityExtension.FlagForDelete(existingItem, user, "Facade");
                                
                                this.dbContext.PurchasingDispositionItems.Update(existingItem);
                                foreach (var existingDetail in existingItem.Details)
                                {
                                    EntityExtension.FlagForDelete(existingDetail, user, "Facade");
                                    
                                    this.dbContext.PurchasingDispositionDetails.Update(existingDetail);
                                }
                            }
                            else
                            {
                                foreach (var existingDetail in existingItem.Details)
                                {
                                    var newDetail = newItem.Details.FirstOrDefault(d => d.Id == existingDetail.Id);
                                    if (newDetail == null)
                                    {
                                        EntityExtension.FlagForDelete(existingDetail, user, "Facade");
                                        
                                        this.dbContext.PurchasingDispositionDetails.Update(existingDetail);

                                    }
                                }
                            }
                        }

                        Updated = await dbContext.SaveChangesAsync();
                        transaction.Commit();

                    }
                    else
                    {
                        throw new Exception("Error");
                    }
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    throw new Exception(e.Message);
                }
            }

            return Updated;
        }
    }
}
