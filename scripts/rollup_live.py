"""
Roll up Live_Parking (the simulator's live data) into the summary/analytics tables
so the dashboard's analytical pages show the latest live activity when filtered to
the current period (e.g. June 2026). Re-run on a schedule (every 1-2 min) or on demand.

It only replaces rows from the live dates onward; your Jan-May 2026 + 2025 history is untouched.
Requires: pip install pandas numpy psycopg2-binary  | edit PASSWORD below.
"""
import pandas as pd, numpy as np
PASSWORD = "parking123"
CONN = f"host=localhost dbname=parking_db user=postgres password={PASSWORD} port=5432"
CAP = 11000
def peak_label(h):
    return ("Morning Peak" if 7<=h<=9 else "Lunch Peak" if 12<=h<=14 else "Evening Peak" if 18<=h<=20
            else "Daytime" if 10<=h<=17 else "Night" if 21<=h<=23 else "Late Night/Early")

def build_rows(live):
    live = live.copy()
    live["Entry_Time"]=pd.to_datetime(live["Entry_Time"]); live["Exit_Time"]=pd.to_datetime(live["Exit_Time"])
    live["d"]=live["Entry_Time"].dt.date; live["h"]=live["Entry_Time"].dt.hour
    live["Parking_Fee"]=pd.to_numeric(live["Parking_Fee"],errors="coerce").fillna(0.0)
    live["Parking_Duration_Hours"]=pd.to_numeric(live["Parking_Duration_Hours"],errors="coerce")
    ent=live["Entry_Time"].astype("int64").values/1e9
    ext=live["Exit_Time"].astype("int64").values/1e9
    ext=np.where(np.isnat(live["Exit_Time"].values), ent.max()+1e9, ext)  # still-parked = far future
    daily=[]; hourly=[]; occ=[]; elog=[]
    for d in sorted(live["d"].unique()):
        sub=live[live["d"]==d]; ts=pd.Timestamp(d)
        dow=ts.weekday(); isw=dow>=5; dt_lbl="Weekend" if isw else "Weekday"; dn=ts.strftime("%A")
        n=len(sub); rev=float(sub["Parking_Fee"].sum())
        avgdur=float(sub["Parking_Duration_Hours"].dropna().mean()) if sub["Parking_Duration_Hours"].notna().any() else 0.0
        ev=(sub["Event_Status"]=="Event Day").any(); status="Event Day" if ev else "Non-Event Day"
        evname=sub.loc[sub["Event_Status"]=="Event Day","Event_Name"].mode()
        evname=evname.iloc[0] if len(evname) else ""
        # concurrency per hour
        peak=0
        for hh in range(24):
            # naive epoch (matches ent/ext from astype("int64"); .timestamp() shifts by the local UTC offset)
            ws=(pd.Timestamp(d)-pd.Timestamp("1970-01-01")).total_seconds()+hh*3600; we=ws+3600
            conc=int(((ent<we)&(ext>ws)).sum())
            arr=int((sub["h"]==hh).sum())
            hrev=float(sub.loc[sub["h"]==hh,"Parking_Fee"].sum())
            hdur=sub.loc[sub["h"]==hh,"Parking_Duration_Hours"].dropna()
            hdur=float(hdur.mean()) if len(hdur) else 0.0
            peak=max(peak,conc)
            if arr>0 or conc>0:
                occ.append((str(d),hh,conc,round(conc/CAP*100,2),dn,dt_lbl,peak_label(hh)))
                hourly.append((str(d),hh,arr,round(hrev,2),round(hdur,2),dn,dt_lbl,peak_label(hh)))
        daily.append((str(d),n,round(rev,2),round(rev/n,2) if n else 0,round(avgdur,2),1 if ev else 0,
                      dn,ts.strftime("%B"),dt_lbl,status,(ts.month-1)//3+1,round(peak/CAP*100,2),round(n/CAP,4)))
        if ev:
            elog.append((str(d),evname,"Event Day",round(rev,2),n))
    # transactions (old 26-col schema)
    txn=[]
    for r in live.itertuples(index=False):
        et=pd.Timestamp(r.Entry_Time); fee=float(r.Parking_Fee or 0); dur=r.Parking_Duration_Hours
        dur=float(dur) if pd.notna(dur) else None
        es=r.Event_Status; en=r.Event_Name or ""
        txn.append((r.Vehicle_ID,str(et),(str(r.Exit_Time) if pd.notna(r.Exit_Time) else None),fee,r.Parking_Level,
            1 if es=="Event Day" else 0,en,r.Vehicle_Type,r.Payment_Type,(round(dur,2) if dur else None),
            str(et.date()),et.year,et.month,et.strftime("%B"),et.day,et.hour,et.strftime("%A"),et.weekday(),
            "Weekend" if et.weekday()>=5 else "Weekday",peak_label(et.hour),(en if es=="Event Day" else "None"),
            es,(et.month-1)//3+1,int(et.strftime("%V")),(round(fee/dur,2) if dur and dur>0 else 0),r.Ticket_ID))
    return daily,hourly,occ,elog,txn

def main():
    import psycopg2
    conn=psycopg2.connect(CONN)
    live=pd.read_sql('SELECT * FROM "Live_Parking"',conn)
    if live.empty:
        print("No live data yet — start the simulator first."); return
    daily,hourly,occ,elog,txn=build_rows(live)
    mind=str(pd.to_datetime(live["Entry_Time"]).dt.date.min())
    cur=conn.cursor()
    for t in ["Daily_Summary","Hourly_Summary","Hourly_Occupancy","Event_Log_Table","Transactions_Cleaned"]:
        cur.execute(f'DELETE FROM "{t}" WHERE "Entry_Date">=%s',(mind,))
    cur.executemany('INSERT INTO "Daily_Summary" ("Entry_Date","Total_Vehicles","Total_Revenue","Average_Fee","Average_Duration_Hours","Event_Flag","Day_Name","Month","Is_Weekend","Event_Status","Quarter","Occupancy_Rate_%%","Turnover_Rate") VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)',daily)
    cur.executemany('INSERT INTO "Hourly_Summary" ("Entry_Date","Entry_Hour","Vehicle_Count","Revenue","Average_Duration_Hours","Day_Name","Is_Weekend","Peak_Period") VALUES (%s,%s,%s,%s,%s,%s,%s,%s)',hourly)
    cur.executemany('INSERT INTO "Hourly_Occupancy" ("Entry_Date","Entry_Hour","Concurrent_Vehicles","Occupancy_Rate_%%","Day_Name","Day_Type","Peak_Period") VALUES (%s,%s,%s,%s,%s,%s,%s)',occ)
    cur.executemany('INSERT INTO "Event_Log_Table" ("Entry_Date","Event_Category","Event_Status","Revenue_RM","Vehicles") VALUES (%s,%s,%s,%s,%s)',elog)
    cur.executemany('INSERT INTO "Transactions_Cleaned" ("Vehicle_ID","Entry_Time","Exit_Time","Parking_Fee","Parking_Level","Event_Flag","Event_Name","Vehicle_Type","Payment_Type","Parking_Duration_Hours","Entry_Date","Entry_Year","Entry_Month_No","Entry_Month","Entry_Day","Entry_Hour","Day_Name","Day_Of_Week_No","Is_Weekend","Peak_Period","Event_Category","Event_Status","Quarter","Entry_Week","Revenue_Per_Hour","Ticket_ID") VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)',txn)
    conn.commit(); conn.close()
    print(f"[OK] Rolled up {len(txn)} live cars into {len(daily)} day(s) from {mind}. Filter the dashboard to that date to see it.")

if __name__=="__main__":
    main()
