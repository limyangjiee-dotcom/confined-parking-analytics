"""Remove demo-imported sessions from Live_Parking (Payment_Type='Imported')."""
import psycopg2
c = psycopg2.connect(host="localhost", port=5432, dbname="parking_db",
                     user="postgres", password="parking123")
cur = c.cursor()
cur.execute('DELETE FROM "Live_Parking" WHERE "Payment_Type"=\'Imported\'')
print("deleted imported rows:", cur.rowcount)
c.commit(); c.close()
