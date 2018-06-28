﻿using Com.DanLiris.Service.Purchasing.Lib.Helpers;
using Com.DanLiris.Service.Purchasing.Lib.Models.DeliveryOrderModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.ExternalPurchaseOrderModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.InternalPurchaseOrderModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.PurchaseRequestModel;
using Com.DanLiris.Service.Purchasing.Lib.Models.UnitReceiptNoteModel;
using Com.Moonlay.Models;
using Com.Moonlay.NetCore.Lib;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Com.DanLiris.Service.Purchasing.Lib.Facades
{
    public class DeliveryOrderFacade
    {

        private readonly PurchasingDbContext dbContext;
        private readonly DbSet<DeliveryOrder> dbSet;
        public readonly IServiceProvider serviceProvider;

        private string USER_AGENT = "Facade";

        public DeliveryOrderFacade(PurchasingDbContext dbContext, IServiceProvider serviceProvider)
        {
            this.dbContext = dbContext;
            this.dbSet = dbContext.Set<DeliveryOrder>();
            this.serviceProvider = serviceProvider;
        }

        public Tuple<List<DeliveryOrder>, int, Dictionary<string, string>> Read(int Page = 1, int Size = 25, string Order = "{}", string Keyword = null, string Filter = "{}")
        {
            IQueryable<DeliveryOrder> Query = this.dbSet;

            Query = Query.Select(s => new DeliveryOrder
            {
                Id = s.Id,
                UId = s.UId,
                DONo = s.DONo,
                DODate = s.DODate,
                ArrivalDate = s.ArrivalDate,
                SupplierName = s.SupplierName,
                IsClosed = s.IsClosed,
                CreatedBy = s.CreatedBy,
                LastModifiedUtc = s.LastModifiedUtc,
                Items = s.Items.Select(i => new DeliveryOrderItem
                {
                    EPOId = i.EPOId,
                    EPONo = i.EPONo
                }).ToList()
            });

            List<string> searchAttributes = new List<string>()
            {
                "DONo", "SupplierName", "Items.EPONo"
            };

            //Query = QueryHelper<DeliveryOrder>.ConfigureSearch(Query, searchAttributes, Keyword); // kalo search setelah Select dan dengan searchAttributes ada "." maka case sensitive, kalo tanpa "." tidak masalah
            Query = QueryHelper<DeliveryOrder>.ConfigureSearch(Query, searchAttributes, Keyword, true); // bisa make ToLower()

            Dictionary<string, string> FilterDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Filter);
            Query = QueryHelper<DeliveryOrder>.ConfigureFilter(Query, FilterDictionary);

            Dictionary<string, string> OrderDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(Order);
            Query = QueryHelper<DeliveryOrder>.ConfigureOrder(Query, OrderDictionary);

            Pageable<DeliveryOrder> pageable = new Pageable<DeliveryOrder>(Query, Page - 1, Size);
            List<DeliveryOrder> Data = pageable.Data.ToList();
            int TotalData = pageable.TotalCount;

            return Tuple.Create(Data, TotalData, OrderDictionary);
        }

        public Tuple<DeliveryOrder, List<long>> ReadById(int id)
        {
            var Result = dbSet.Where(m => m.Id == id)
                .Include(m => m.Items)
                    .ThenInclude(i => i.Details)
                .FirstOrDefault();

            List<long> unitReceiptNoteIds = dbContext.UnitReceiptNotes.Where(m => m.DOId == id && m.IsDeleted == false).Select(m => m.Id).ToList();

            return Tuple.Create(Result, unitReceiptNoteIds);
        }

        public async Task<int> Create(DeliveryOrder model, string username)
        {
            int Created = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    EntityExtension.FlagForCreate(model, username, USER_AGENT);

                    foreach (var item in model.Items)
                    {
                        EntityExtension.FlagForCreate(item, username, USER_AGENT);

                        foreach (var detail in item.Details)
                        {
                            EntityExtension.FlagForCreate(detail, username, USER_AGENT);

                            ExternalPurchaseOrderDetail externalPurchaseOrderDetail = this.dbContext.ExternalPurchaseOrderDetails.SingleOrDefault(m => m.Id == detail.EPODetailId);
                            externalPurchaseOrderDetail.DOQuantity += detail.DOQuantity;
                            EntityExtension.FlagForUpdate(externalPurchaseOrderDetail, username, USER_AGENT);
                            SetStatus(externalPurchaseOrderDetail, detail, username);
                        }
                    }

                    this.dbSet.Add(model);
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

        public async Task<int> Update(int id, DeliveryOrder model, string user)
        {
            int Updated = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    var existingModel = this.dbSet.AsNoTracking()
                        .Include(d => d.Items)
                            .ThenInclude(d => d.Details)
                        .SingleOrDefault(pr => pr.Id == id && !pr.IsDeleted);

                    if (existingModel != null && id == model.Id)
                    {
                        EntityExtension.FlagForUpdate(model, user, USER_AGENT);

                        foreach (var item in model.Items.ToList())
                        {
                            var existingItem = existingModel.Items.SingleOrDefault(m => m.Id == item.Id);
                            List<DeliveryOrderItem> duplicateDeliveryOrderItems = model.Items.Where(i => i.EPOId == item.EPOId && i.Id != item.Id).ToList();

                            if (item.Id == 0)
                            {
                                if (duplicateDeliveryOrderItems.Count <= 0)
                                {
                                    EntityExtension.FlagForCreate(item, user, USER_AGENT);

                                    foreach (var detail in item.Details)
                                    {
                                        EntityExtension.FlagForCreate(detail, user, USER_AGENT);

                                        ExternalPurchaseOrderDetail externalPurchaseOrderDetail = this.dbContext.ExternalPurchaseOrderDetails.SingleOrDefault(m => m.Id == detail.EPODetailId);
                                        externalPurchaseOrderDetail.DOQuantity += detail.DOQuantity;
                                        EntityExtension.FlagForUpdate(externalPurchaseOrderDetail, user, USER_AGENT);
                                        SetStatus(externalPurchaseOrderDetail, detail, user);
                                    }
                                }
                            }
                            else
                            {
                                EntityExtension.FlagForUpdate(item, user, USER_AGENT);

                                if (duplicateDeliveryOrderItems.Count > 0)
                                {
                                    foreach (var detail in item.Details.ToList())
                                    {
                                        if (detail.Id != 0)
                                        {
                                            EntityExtension.FlagForUpdate(detail, user, USER_AGENT);

                                            foreach (var duplicateItem in duplicateDeliveryOrderItems.ToList())
                                            {
                                                foreach (var duplicateDetail in duplicateItem.Details.ToList())
                                                {
                                                    if (detail.ProductId.Equals(duplicateDetail.ProductId))
                                                    {
                                                        detail.DOQuantity += duplicateDetail.DOQuantity;
                                                        detail.ProductRemark = String.Concat(detail.ProductRemark, Environment.NewLine, duplicateDetail.ProductRemark).Trim();

                                                        ExternalPurchaseOrderDetail externalPurchaseOrderDetail = this.dbContext.ExternalPurchaseOrderDetails.SingleOrDefault(m => m.Id == detail.EPODetailId);
                                                        var existingDetail = existingItem.Details.SingleOrDefault(m => m.Id == detail.Id);
                                                        externalPurchaseOrderDetail.DOQuantity = externalPurchaseOrderDetail.DOQuantity - existingDetail.DOQuantity + detail.DOQuantity;
                                                        EntityExtension.FlagForUpdate(externalPurchaseOrderDetail, user, USER_AGENT);
                                                        SetStatus(externalPurchaseOrderDetail, detail, user);
                                                    }
                                                    else if(item.Details.Count(d => d.ProductId.Equals(duplicateDetail.ProductId)) < 1)
                                                    {
                                                        EntityExtension.FlagForCreate(duplicateDetail, user, USER_AGENT);
                                                        item.Details.Add(duplicateDetail);

                                                        ExternalPurchaseOrderDetail externalPurchaseOrderDetail = this.dbContext.ExternalPurchaseOrderDetails.SingleOrDefault(m => m.Id == duplicateDetail.EPODetailId);
                                                        externalPurchaseOrderDetail.DOQuantity += duplicateDetail.DOQuantity;
                                                        EntityExtension.FlagForUpdate(externalPurchaseOrderDetail, user, USER_AGENT);
                                                        SetStatus(externalPurchaseOrderDetail, duplicateDetail, user);
                                                    }
                                                }
                                                model.Items.Remove(duplicateItem);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (var detail in item.Details)
                                    {
                                        if (detail.Id != 0)
                                        {
                                            EntityExtension.FlagForUpdate(detail, user, USER_AGENT);

                                            var existingDetail = existingItem.Details.SingleOrDefault(m => m.Id == detail.Id);

                                            ExternalPurchaseOrderDetail externalPurchaseOrderDetail = this.dbContext.ExternalPurchaseOrderDetails.SingleOrDefault(m => m.Id == detail.EPODetailId);
                                            externalPurchaseOrderDetail.DOQuantity = externalPurchaseOrderDetail.DOQuantity - existingDetail.DOQuantity + detail.DOQuantity;
                                            EntityExtension.FlagForUpdate(externalPurchaseOrderDetail, user, USER_AGENT);
                                            SetStatus(externalPurchaseOrderDetail, detail, user);
                                        }
                                    }
                                }
                            }
                        }

                        this.dbContext.Update(model);

                        foreach (var existingItem in existingModel.Items)
                        {
                            var newItem = model.Items.FirstOrDefault(i => i.Id == existingItem.Id);
                            if (newItem == null)
                            {
                                EntityExtension.FlagForDelete(existingItem, user, USER_AGENT);
                                this.dbContext.DeliveryOrderItems.Update(existingItem);
                                foreach (var existingDetail in existingItem.Details)
                                {
                                    EntityExtension.FlagForDelete(existingDetail, user, USER_AGENT);
                                    this.dbContext.DeliveryOrderDetails.Update(existingDetail);

                                    ExternalPurchaseOrderDetail externalPurchaseOrderDetail = this.dbContext.ExternalPurchaseOrderDetails.SingleOrDefault(m => m.Id == existingDetail.EPODetailId);
                                    externalPurchaseOrderDetail.DOQuantity -= existingDetail.DOQuantity;
                                    EntityExtension.FlagForUpdate(externalPurchaseOrderDetail, user, USER_AGENT);
                                    SetStatus(externalPurchaseOrderDetail, existingDetail, user);
                                }
                            }
                            else
                            {
                                foreach (var existingDetail in existingItem.Details)
                                {
                                    var newDetail = newItem.Details.FirstOrDefault(d => d.Id == existingDetail.Id);
                                    if (newDetail == null)
                                    {
                                        EntityExtension.FlagForDelete(existingDetail, user, USER_AGENT);
                                        this.dbContext.DeliveryOrderDetails.Update(existingDetail);

                                        ExternalPurchaseOrderDetail externalPurchaseOrderDetail = this.dbContext.ExternalPurchaseOrderDetails.SingleOrDefault(m => m.Id == existingDetail.EPODetailId);
                                        externalPurchaseOrderDetail.DOQuantity -= existingDetail.DOQuantity;
                                        EntityExtension.FlagForUpdate(externalPurchaseOrderDetail, user, USER_AGENT);
                                        SetStatus(externalPurchaseOrderDetail, existingDetail, user);
                                    }
                                }
                            }
                        }

                        Updated = await dbContext.SaveChangesAsync();
                        transaction.Commit();
                    }
                    else
                    {
                        throw new Exception("Invalid Id");
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

        public int Delete(int id, string username)
        {
            int Deleted = 0;

            using (var transaction = this.dbContext.Database.BeginTransaction())
            {
                try
                {
                    var model = this.dbSet
                        .Include(d => d.Items)
                            .ThenInclude(d => d.Details)
                        .SingleOrDefault(pr => pr.Id == id && !pr.IsDeleted);

                    EntityExtension.FlagForDelete(model, username, USER_AGENT);

                    foreach (var item in model.Items)
                    {
                        EntityExtension.FlagForDelete(item, username, USER_AGENT);
                        foreach (var detail in item.Details)
                        {
                            ExternalPurchaseOrderDetail externalPurchaseOrderDetail = this.dbContext.ExternalPurchaseOrderDetails.SingleOrDefault(m => m.Id == detail.EPODetailId);
                            externalPurchaseOrderDetail.DOQuantity -= detail.DOQuantity;
                            EntityExtension.FlagForUpdate(externalPurchaseOrderDetail, username, USER_AGENT);
                            SetStatus(externalPurchaseOrderDetail, detail, username);

                            EntityExtension.FlagForDelete(detail, username, USER_AGENT);
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

        private void SetStatus(ExternalPurchaseOrderDetail externalPurchaseOrderDetail, DeliveryOrderDetail detail, string username)
        {
            PurchaseRequestItem purchaseRequestItem = this.dbContext.PurchaseRequestItems.SingleOrDefault(i => i.Id == detail.PRItemId);
            InternalPurchaseOrderItem internalPurchaseOrderItem = this.dbContext.InternalPurchaseOrderItems.SingleOrDefault(i => i.Id == detail.POItemId);

            if (externalPurchaseOrderDetail.DOQuantity == 0)
            {
                purchaseRequestItem.Status = "Sudah diorder ke Supplier";
                internalPurchaseOrderItem.Status = "Sudah diorder ke Supplier";

                EntityExtension.FlagForUpdate(purchaseRequestItem, username, USER_AGENT);
                EntityExtension.FlagForUpdate(internalPurchaseOrderItem, username, USER_AGENT);
            }
            else if (externalPurchaseOrderDetail.DOQuantity > 0 && externalPurchaseOrderDetail.DOQuantity < externalPurchaseOrderDetail.DealQuantity)
            {
                purchaseRequestItem.Status = "Barang sudah datang parsial";
                internalPurchaseOrderItem.Status = "Barang sudah datang parsial";

                EntityExtension.FlagForUpdate(purchaseRequestItem, username, USER_AGENT);
                EntityExtension.FlagForUpdate(internalPurchaseOrderItem, username, USER_AGENT);
            }
            else if (externalPurchaseOrderDetail.DOQuantity > 0 && externalPurchaseOrderDetail.DOQuantity >= externalPurchaseOrderDetail.DealQuantity)
            {
                purchaseRequestItem.Status = "Barang sudah datang semua";
                internalPurchaseOrderItem.Status = "Barang sudah datang semua";

                EntityExtension.FlagForUpdate(purchaseRequestItem, username, USER_AGENT);
                EntityExtension.FlagForUpdate(internalPurchaseOrderItem, username, USER_AGENT);
            }
        }

    }
}