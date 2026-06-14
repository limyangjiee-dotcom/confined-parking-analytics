"""
Auto-retrain the parking forecast from the latest PostgreSQL data and write it
back into the Forecast_30Days table. Designed to be run on a schedule (Windows
Task Scheduler). Forecasts the next 30 days from the most recent data date.

Requires: pip install pandas numpy scikit-learn psycopg2-binary
Edit PASSWORD below to your postgres password.
"""
import numpy as np, pandas as pd
from datetime import timedelta
from sklearn.ensemble import HistGradientBoostingRegressor

PASSWORD = "parking123"      # <-- your postgres password
CONN = f"host=localhost dbname=parking_db user=postgres password={PASSWORD} port=5432"
CAP = 11000
HOL = {"2026-01-01","2026-02-01","2026-02-17","2026-02-18","2026-03-21","2026-03-22",
       "2026-05-01","2026-05-31","2026-06-01","2026-06-07","2026-08-31","2026-09-16",
       "2026-10-09","2026-12-25"}

def build_forecast(daily, hourly):
    d = daily.copy(); d["Entry_Date"] = pd.to_datetime(d["Entry_Date"])
    d = d.sort_values("Entry_Date").reset_index(drop=True)
    d["dow"]=d.Entry_Date.dt.weekday; d["month"]=d.Entry_Date.dt.month
    d["is_weekend"]=(d.dow>=5).astype(int); d["is_friday"]=(d.dow==4).astype(int)
    d["is_event"]=d["Event_Flag"].astype(int)
    d["is_holiday"]=d.Entry_Date.dt.strftime("%Y-%m-%d").isin(HOL).astype(int)
    d["flat"]=((d.is_weekend==1)|(d.is_friday==1)|(d.is_holiday==1)).astype(int)
    for c in ["Total_Vehicles","Average_Fee"]:
        d[c+"_l1"]=d[c].shift(1); d[c+"_l7"]=d[c].shift(7); d[c+"_r7"]=d[c].shift(1).rolling(7).mean()
    d=d.dropna(subset=[c for c in d.columns if c.endswith(("_l1","_l7","_r7"))]).reset_index(drop=True)
    FD=["dow","month","is_weekend","is_friday","is_event","is_holiday","flat"]
    def fitm(t):
        f=FD+[t+"_l1",t+"_l7",t+"_r7"]
        return HistGradientBoostingRegressor(max_iter=400,learning_rate=0.05,max_depth=5,random_state=42).fit(d[f],d[t]),f
    mveh,fveh=fitm("Total_Vehicles"); mfee,ffee=fitm("Average_Fee")

    h=hourly.copy(); h["Entry_Date"]=pd.to_datetime(h["Entry_Date"])
    h["dt"]=h.Entry_Date+pd.to_timedelta(h.Entry_Hour,unit="h"); h=h.sort_values("dt").reset_index(drop=True)
    h["dow"]=h.Entry_Date.dt.weekday; h["month"]=h.Entry_Date.dt.month
    h["is_weekend"]=(h.dow>=5).astype(int); h["is_friday"]=(h.dow==4).astype(int)
    h["is_holiday"]=h.Entry_Date.dt.strftime("%Y-%m-%d").isin(HOL).astype(int)
    h["hs"]=np.sin(2*np.pi*h.Entry_Hour/24); h["hc"]=np.cos(2*np.pi*h.Entry_Hour/24)
    h["lag24"]=h["Occupancy_Rate_%"].shift(24); h["lag168"]=h["Occupancy_Rate_%"].shift(168)
    h["roll24"]=h["Occupancy_Rate_%"].shift(1).rolling(24).mean()
    h=h.dropna(subset=["lag24","lag168","roll24"]).reset_index(drop=True)
    FH=["Entry_Hour","dow","month","is_weekend","is_friday","is_holiday","hs","hc","lag24","lag168","roll24"]
    mh=HistGradientBoostingRegressor(max_iter=400,learning_rate=0.05,max_depth=6,random_state=42).fit(h[FH],h["Occupancy_Rate_%"])

    start=d["Entry_Date"].max()+timedelta(days=1)
    veh=list(d["Total_Vehicles"]); fee=list(d["Average_Fee"]); ho=list(h["Occupancy_Rate_%"]); rows=[]
    cur=start
    for i in range(30):
        ds=cur.strftime("%Y-%m-%d"); dow=cur.weekday()
        base={"dow":dow,"month":cur.month,"is_weekend":int(dow>=5),"is_friday":int(dow==4),
              "is_event":0,"is_holiday":int(ds in HOL),"flat":int(dow>=5 or dow==4 or ds in HOL)}
        fv={**base,"Total_Vehicles_l1":veh[-1],"Total_Vehicles_l7":veh[-7],"Total_Vehicles_r7":np.mean(veh[-7:])}
        ff={**base,"Average_Fee_l1":fee[-1],"Average_Fee_l7":fee[-7],"Average_Fee_r7":np.mean(fee[-7:])}
        pv=float(mveh.predict(pd.DataFrame([fv])[fveh])[0]); pf=float(mfee.predict(pd.DataFrame([ff])[ffee])[0])
        veh.append(pv); fee.append(pf)
        peak=0.0
        for hh in range(24):
            row={"Entry_Hour":hh,"dow":dow,"month":cur.month,"is_weekend":int(dow>=5),"is_friday":int(dow==4),
                 "is_holiday":int(ds in HOL),"hs":np.sin(2*np.pi*hh/24),"hc":np.cos(2*np.pi*hh/24),
                 "lag24":ho[-24],"lag168":ho[-168],"roll24":np.mean(ho[-24:])}
            p=float(np.clip(mh.predict(pd.DataFrame([row])[FH])[0],0,100)); ho.append(p); peak=max(peak,p)
        rows.append((ds,dow,cur.strftime("%B"),int(dow>=5),int(ds in HOL),round(pf,2),round(pv/CAP,3),
                     round(peak,2),round(pv*pf,2),"Weekend" if dow>=5 else "Weekday",cur.strftime("%A")))
        cur+=timedelta(days=1)
    return rows

def main():
    import psycopg2
    conn=psycopg2.connect(CONN)
    daily=pd.read_sql('SELECT "Entry_Date","Total_Vehicles","Total_Revenue","Average_Fee","Event_Flag","Occupancy_Rate_%" FROM "Daily_Summary" ORDER BY "Entry_Date"',conn)
    hourly=pd.read_sql('SELECT "Entry_Date","Entry_Hour","Occupancy_Rate_%" FROM "Hourly_Occupancy" ORDER BY "Entry_Date","Entry_Hour"',conn)
    rows=build_forecast(daily,hourly)
    cur=conn.cursor(); cur.execute('TRUNCATE "Forecast_30Days"')
    cur.executemany('INSERT INTO "Forecast_30Days" ("Forecast_Date","Day_Of_Week_No","Month","Is_Weekend_Flag","Is_Event_Day","AvgFee","TurnoverRate","Predicted_Occupancy_Rate_%%","Predicted_Revenue_RM","Is_Weekend","Day_Name") VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)', rows)
    conn.commit(); conn.close()
    print(f"[OK] Forecast retrained: {len(rows)} days from {rows[0][0]} to {rows[-1][0]}")

if __name__=="__main__":
    main()
