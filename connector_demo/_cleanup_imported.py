"""Remove demo-imported sessions from Live_Parking (Ticket_ID prefix 'IMP')."""
import psycopg2
c = psycopg2.connect(host="localhost", port=5432, dbname="parking_db",
                     user="postgres", password="parking123")
cur = c.cursor()
cur.execute('DELETE FROM "Live_Parking" WHERE "Ticket_ID" LIKE \'IMP%\' OR "Payment_Type"=\'Imported\'')
print("deleted imported rows:", cur.rowcount)
c.commit(); c.close()
